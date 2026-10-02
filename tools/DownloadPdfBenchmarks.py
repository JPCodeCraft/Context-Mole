"""Fetch pinned PDF benchmark inputs; Python standard library only.

PDFs and annotations stay in an explicitly selected cache, outside the checkout.
The ViDoRe viewer is used only when its x-revision header matches the immutable
dataset revision. Image bytes are not downloaded; supplied OCR markdown is
discarded and never indexed or used as extraction ground truth.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import time
import urllib.parse
import urllib.request


class InputError(ValueError):
    pass


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_path(root, relative):
    parts = PurePosixPath(relative)
    if not relative or parts.is_absolute() or ".." in parts.parts or "\\" in relative or ":" in relative:
        raise InputError(f"Unsafe cache path: {relative!r}")
    result = root.joinpath(*parts.parts).resolve()
    if not result.is_relative_to(root.resolve()):
        raise InputError(f"Cache path escaped its root: {relative!r}")
    return result


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".partial")
    temporary.write_bytes(canonical(value) + b"\n")
    temporary.replace(path)


def request(url, revision=None):
    req = urllib.request.Request(url, headers={"User-Agent": "ContextMole-PdfBenchmarks/1.0"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=60) as response:
                if revision and response.headers.get("x-revision") != revision:
                    raise InputError("Annotation viewer revision differs from the locked dataset; refusing unpinned rows.")
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 3:
                raise
            delay = min(60, max(2 ** (attempt + 1), int(error.headers.get("Retry-After", "5"))))
            print(f"Public data service returned {error.code}; retrying in {delay}s", flush=True)
            time.sleep(delay)


def verify(data, expected, label):
    if expected and digest(data) != expected:
        raise InputError(f"SHA-256 mismatch for {label}; do not silently update the lock.")
    return data


def download(dataset, revision, relative, destination, expected=None, offline=False):
    if destination.exists():
        return verify(destination.read_bytes(), expected, str(destination))
    if offline:
        raise InputError(f"Offline input missing: {destination}")
    url = f"https://huggingface.co/datasets/{dataset}/resolve/{revision}/{urllib.parse.quote(relative, safe='/')}"
    try:
        data = verify(request(url), expected, relative)
    except urllib.error.URLError as error:
        raise InputError(f"Download failed for {relative} at the pinned dataset revision: {error}") from error
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".partial")
    temporary.write_bytes(data)
    temporary.replace(destination)
    print(f"Downloaded {relative}: {len(data):,} bytes", flush=True)
    return data


def normalized_row(config, row):
    if config == "corpus":
        image = row["image"]
        return {"corpus_id": row["corpus_id"], "doc_id": row["doc_id"],
                "page_number_in_doc": row["page_number_in_doc"],
                "width": image["width"], "height": image["height"]}
    return row


def viewer_batch(dataset, revision, config, offset, where=None):
    query = urllib.parse.urlencode({"dataset": dataset, "config": config,
                                   "split": "test", "offset": offset, "length": 100})
    if where:
        query += "&" + urllib.parse.urlencode({"where": where})
    endpoint = "filter" if where else "rows"
    payload = json.loads(request("https://datasets-server.huggingface.co/" + endpoint + "?" + query, revision))
    if payload.get("partial"):
        raise InputError(f"Incomplete viewer response: {config}/{offset}")
    rows = []
    for item in payload["rows"]:
        # OCR markdown is intentionally omitted, so its truncation is immaterial.
        truncated = set(item.get("truncated_cells", [])) - ({"markdown"} if config == "corpus" else set())
        if truncated:
            raise InputError(f"Truncated annotation fields in {config}/{offset}: {sorted(truncated)}")
        rows.append(normalized_row(config, item["row"]))
    return payload["num_rows_total"], rows


def viewer_rows(entry, config, cache, offline=False):
    path = cache / "sources" / (config + ".json")
    expected = entry["annotation_sha256"].get(config)
    if path.exists():
        return json.loads(verify(path.read_bytes().rstrip(b"\n"), expected, str(path)))
    if offline:
        raise InputError(f"Offline annotations missing: {path}")
    where = entry.get("row_filters", {}).get(config)
    batch_root = cache / "sources" / "batches" / config / digest((where or "all").encode())[:12]
    def batch(offset):
        batch_path = batch_root / f"{offset}.json"
        if batch_path.exists():
            payload = json.loads(batch_path.read_bytes())
            if payload["revision"] != entry["revision"]:
                raise InputError("Cached annotation batch revision differs from the lock.")
            return payload["total"], payload["rows"]
        total, rows = viewer_batch(entry["dataset"], entry["revision"], config, offset, where)
        write_json(batch_path, {"revision": entry["revision"], "total": total, "rows": rows})
        return total, rows
    count, first = batch(0)
    offsets = list(range(100, count, 100))
    with ThreadPoolExecutor(max_workers=4) as pool:
        batches = list(pool.map(batch, offsets))
    if any(total != count for total, _ in batches):
        raise InputError("Annotation row count changed during a pinned download.")
    rows = first + [row for _, batch in batches for row in batch]
    if len(rows) != count:
        raise InputError(f"Expected {count} {config} rows, received {len(rows)}.")
    data = verify(canonical(rows), expected, config)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data + b"\n")
    print(f"Cached {config}: {count} rows; SHA-256 {digest(data)}", flush=True)
    return rows


def normalized_box(box, page):
    width, height = page["width"], page["height"]
    if width <= 0 or height <= 0:
        raise InputError("Page image dimensions must be positive.")
    values = [box["x1"] / width, box["y1"] / height, box["x2"] / width, box["y2"] / height]
    if not (0 <= values[0] < values[2] <= 1 and 0 <= values[1] < values[3] <= 1):
        raise InputError(f"Invalid annotated bounding box: {box}")
    return values


def vidore_manifest(entry, documents, pages, queries, qrels, subset):
    doc_by_id = {doc["doc_id"]: doc for doc in documents}
    page_by_id = {page["corpus_id"]: page for page in pages}
    if len(doc_by_id) != len(documents) or len(page_by_id) != len(pages):
        raise InputError("Duplicate document or page IDs.")
    document_ids = set(entry["smoke_document_ids"]) if subset == "smoke" else set(doc_by_id)
    if not document_ids <= set(doc_by_id):
        raise InputError("Locked smoke documents are absent from this revision.")
    selected_pages = {key: page for key, page in page_by_id.items() if page["doc_id"] in document_ids}
    by_query = {}
    for qrel in qrels:
        if qrel["score"] not in (1, 2) or qrel["corpus_id"] not in page_by_id:
            raise InputError("Unexpected relevance score or missing page mapping.")
        by_query.setdefault(qrel["query_id"], []).append(qrel)
    selected_queries = []
    box_diagnostics = []
    def project_qrel(row):
        rectangles = []
        for box in row.get("bounding_boxes", []):
            if box["x1"] == box["x2"] or box["y1"] == box["y2"]:
                box_diagnostics.append({"query_id": row["query_id"], "corpus_id": row["corpus_id"],
                    "reason": "zero_area", "original_pixel_box": box})
                continue
            rectangles.append(normalized_box(box, page_by_id[row["corpus_id"]]))
        return {"corpus_id": row["corpus_id"], "score": row["score"],
                "bounding_boxes": rectangles, "content_type": row.get("content_type", [])}
    smoke_ids = set(entry["smoke_query_ids"])
    languages = set(entry.get("languages", [query["language"] for query in queries]))
    for query in queries:
        if query["language"] not in languages:
            continue
        if subset == "smoke" and query["query_id"] not in smoke_ids:
            continue
        relevant = by_query.get(query["query_id"], [])
        if not relevant:
            raise InputError(f"Query {query['query_id']} has no relevance judgments.")
        # A small corpus is a closed subset: never remove out-of-subset positives.
        if any(row["corpus_id"] not in selected_pages for row in relevant):
            if subset == "smoke":
                raise InputError(f"Locked smoke query {query['query_id']} has a relevant page outside the smoke corpus.")
            raise InputError("A full query references an absent page.")
        selected_queries.append({"id": str(query["query_id"]), "text": query["query"],
            "language": query["language"], "types": query.get("query_types", []),
            "format": query.get("query_format"), "content_type": query.get("content_type", []),
            "reference_answer": query.get("answer"), "reference_answer_used_for_scoring": False,
            "qrels": [project_qrel(row) for row in relevant]})
    if subset == "smoke" and {int(query["id"]) for query in selected_queries} != smoke_ids:
        raise InputError("The locked smoke query selection is incomplete.")
    pdfs = []
    for key in sorted(document_ids):
        doc = doc_by_id[key]
        if not doc.get("license"):
            raise InputError(f"PDF publisher license is missing: {key}")
        path = "pdfs/" + doc["file_name"]
        locked = entry["pdfs"][path]
        pdfs.append({"id": key, "file": path, "sha256": locked["sha256"],
                     "license": doc["license"], "page_count": doc["page_number"], "source_url": doc["url"]})
    return {"version": 1, "dataset": entry["dataset"], "revision": entry["revision"], "selection": subset,
        "annotation_license": "CC-BY-4.0", "coordinates_frame": "imageNormalizedTopLeft",
        "supplied_ocr_markdown_used": False, "reference_answers_scored": False,
        "unusable_bounding_box_count": len(box_diagnostics), "bbox_diagnostics": box_diagnostics,
        "annotation_warnings": ([f"{len(box_diagnostics)} zero-area gold rectangles cannot contribute geometric area; original pixel boxes remain in bbox_diagnostics and all page relevance labels are retained."] if box_diagnostics else []),
        "annotation_sha256": entry["annotation_sha256"], "query_languages": sorted(languages), "documents": pdfs,
        "pages": [{"corpus_id": key, "document_id": page["doc_id"],
            "page_number": page["page_number_in_doc"] + 1, "width": page["width"], "height": page["height"]}
            for key, page in sorted(selected_pages.items())], "queries": selected_queries}


def prepare_vidore(entry, cache, subset, offline, workers=4):
    data = {config: viewer_rows(entry, config, cache, offline) for config in
            ["documents_metadata", "corpus", "queries", "qrels"]}
    manifest = vidore_manifest(entry, data["documents_metadata"], data["corpus"], data["queries"], data["qrels"], subset)
    destination = cache / subset
    def fetch(doc):
        download(entry["dataset"], entry["revision"], doc["file"], safe_path(destination, doc["file"]), doc["sha256"], offline)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        list(pool.map(fetch, manifest["documents"]))
    write_json(destination / "manifest.json", manifest)
    print(f"ViDoRe {subset}: {len(manifest['documents'])} PDFs, {len(manifest['pages'])} pages, {len(manifest['queries'])} queries", flush=True)


def prepare_olmocr(entry, cache, subset, offline, workers=4):
    checks = []
    categories = {}
    for item in entry["annotations"]:
        data = download(entry["dataset"], entry["revision"], item["path"],
                        safe_path(cache / "sources", item["path"]), item["sha256"], offline)
        rows = [json.loads(line) for line in data.splitlines() if line.strip()]
        selected = set(entry["smoke_pdf_ids"]) if subset == "smoke" else {row["pdf"] for row in rows}
        for row in rows:
            if row["pdf"] in selected:
                checks.append(row)
                categories[row["pdf"]] = item["category"]
    destination = cache / subset
    documents = []
    def fetch(item):
        key, category = item
        path = "bench_data/pdfs/" + key
        expected = entry["pdf_sha256"][key]
        data = download(entry["dataset"], entry["revision"], path,
                        safe_path(destination, "pdfs/" + key), expected, offline)
        return {"id": key, "file": "pdfs/" + key, "sha256": digest(data), "category": category}
    with ThreadPoolExecutor(max_workers=workers) as pool:
        documents = list(pool.map(fetch, sorted(categories.items())))
    check_data = b"".join(canonical(row) + b"\n" for row in checks)
    (destination / "checks.jsonl").write_bytes(check_data)
    manifest = {"version": 1, "dataset": entry["dataset"], "revision": entry["revision"], "selection": subset,
        "license": "ODC-BY-1.0", "documents": documents, "checks_file": "checks.jsonl",
        "checks_sha256": digest(check_data), "upstream_evaluator_revision": entry["evaluator_revision"]}
    write_json(destination / "manifest.json", manifest)
    print(f"olmOCR {subset}: {len(documents)} PDFs, {len(checks)} checks", flush=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", required=True, type=Path, help="Explicit external cache directory; never application data or the checkout")
    parser.add_argument("--dataset", choices=["all", "olmocr", "vidore"], default="all")
    parser.add_argument("--subset", choices=["smoke", "full"], default="smoke")
    parser.add_argument("--offline", action="store_true", help="Verify/rebuild manifests from cached inputs without network")
    parser.add_argument("--workers", type=int, default=4, help="Concurrent PDF downloads (1-8); hashes and manifest ordering remain deterministic")
    args = parser.parse_args(argv)
    if not 1 <= args.workers <= 8:
        raise InputError("--workers must be between 1 and 8.")
    repo = Path(__file__).resolve().parent.parent
    cache = args.cache.resolve()
    if cache.is_relative_to(repo):
        raise InputError("Choose a cache outside the repository checkout.")
    lock = json.loads((repo / "benchmarks/pdf/datasets.lock.json").read_text(encoding="utf-8"))
    for name, prepare in [("olmocr", prepare_olmocr), ("vidore", prepare_vidore)]:
        if args.dataset not in ("all", name):
            continue
        entry = lock[name]
        if not args.offline:
            download(entry["dataset"], entry["revision"], "README.md", cache / name / entry["revision"] / "DATASET-CARD.md", entry["card_sha256"])
        prepare(entry, cache / name / entry["revision"], args.subset, args.offline, args.workers)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (InputError, OSError, KeyError, urllib.error.URLError) as error:
        print(f"Benchmark input error: {error}", file=sys.stderr)
        sys.exit(1)
