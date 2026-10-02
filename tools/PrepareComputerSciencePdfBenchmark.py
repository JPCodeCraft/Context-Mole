"""Reproduce the frozen ViDoRe Computer Science original-PDF panel.

Only an explicitly selected external cache is written. Optional PyArrow and
PyMuPDF imports use the normal Python environment; no dependency is installed.
This adapts the original score-blind prepare_panel.py, without model inference.
"""
from __future__ import annotations

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import hashlib
import importlib
import json
from pathlib import Path
import sys
import urllib.error

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
import DownloadPdfBenchmarks as source

DATASET = "vidore/vidore_v3_computer_science"
REVISION = "d5cc75883d92e294f0c0fc2662551c9708a06ebc"
SEED = "ContextMole-second-domain-v1"
COUNT = 48
PROTOCOL_SHA256 = "ab9226fc5c67dddc2eb3b64b09edaca2531cec66eca5378543bbdc18d8532203"
SELECTION_SHA256 = "fc02b3c46134d967be41cdc08fd9684253231677c6d13d8efcb7b82e759a2327"
MANIFEST_SHA256 = "794ea81c5c0808ae623ec2a6e2adfcff93894ace4d0c18763e924e02b05e012f"
PACKAGED_SHA256 = {
    "selection-protocol.json": PROTOCOL_SHA256,
    "selection.json": SELECTION_SHA256,
    "provenance.lock.json": "0002843360a83c06146c674733d8bff23bb6ba3342a2ad4eaf1fc20dc6d5c805",
    "hf-tree.json": "f01b8359ec4ac742597654e5b6b318df5f139010ef4b0e0f7db92aafb4cda6a4",
    "corpus.json": "fc9b5c1b0555135d45c4007ba5f300ae5efafa378584d9692e615bfe00701413",
}
PINNED = {
    "documents_metadata/test-00000-of-00001.parquet": (5646, "f86f8a0e2ecf6e1ba76965d684ec8798cbb7560b34c5308f16256c53c65b3377"),
    "queries/test-00000-of-00001.parquet": (419894, "86a800279c12f4a607378fc001e89e1d136d5568580c16cc0356eaa0092676f5"),
    "qrels/test-00000-of-00001.parquet": (63107, "338306ef0bc1f3d40f1a4cdfe56aaefa04f512e8d003da88a28c423c7d0514ad"),
    "pdfs/Introduction_to_Computer_Science.pdf": (53398130, "1003e2a5484f0ef1604fa83cf8176066b156ae686560f72ee48376e341395272"),
    "pdfs/Introduction_to_Python_Programming.pdf": (11572327, "ca60aa86a3066f8504eb6e177d16303fcb3e38ff9bd6b07326f4eced94c2f6fc"),
}


def load_dependencies():
    """Fail with an actionable message, while keeping --help/tests dependency-free."""
    try:
        pq = importlib.import_module("pyarrow.parquet")
        fitz = importlib.import_module("fitz")
    except ImportError as error:
        raise source.InputError(
            "This reproducer needs optional PyArrow and PyMuPDF in the active "
            "Python environment (reference versions: pyarrow==21.0.0, "
            "PyMuPDF==1.26.6). Install them separately or supply an existing "
            f"installation through PYTHONPATH. No packages were installed. {error}"
        ) from error
    return pq, fitz


def load_packaged_inputs(directory=None):
    directory = directory or REPO / "benchmarks/pdf/vidore_computer_science"
    data = {name: source.verify((directory / name).read_bytes(), sha, name)
            for name, sha in PACKAGED_SHA256.items()}
    protocol = json.loads(data["selection-protocol.json"])
    if (protocol["dataset"], protocol["revision"], protocol["selection_seed"],
            protocol["independent_original_query_count"]) != (DATASET, REVISION, SEED, COUNT):
        raise source.InputError("Protocol constants differ from the frozen panel.")
    lock = json.loads(data["provenance.lock.json"])
    if (lock["manifest_sha256"], lock["selection_sha256"], lock["protocol_sha256"]) != (
            MANIFEST_SHA256, SELECTION_SHA256, PROTOCOL_SHA256):
        raise source.InputError("Provenance identity differs from the frozen panel.")
    tree = {row["path"]: row for row in json.loads(data["hf-tree.json"])}
    for path, (size, sha) in PINNED.items():
        if tree[path]["size"] != size or tree[path].get("lfs", {}).get("oid") != sha:
            raise source.InputError(f"Official source binary identity changed: {path}")
    source.verify(data["corpus.json"].rstrip(b"\n"), lock["annotation_sha256"]["corpus"], "corpus projection")
    return data, protocol, lock


def retain_locked_file(path, data, expected):
    """Never relock a changed existing file or rewrite an identical input."""
    source.verify(data, expected, str(path))
    if path.exists():
        source.verify(path.read_bytes(), expected, str(path))
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".partial")
    temporary.write_bytes(data)
    temporary.replace(path)


def select_queries(queries, qrels, protocol):
    """Keep the original identity-only rank and complete translation checks."""
    if len({row["query_id"] for row in queries}) != len(queries):
        raise source.InputError("Duplicate source query IDs.")
    english = sorted((r for r in queries if r["language"] == "english"), key=lambda r: r["query_id"])
    portuguese = sorted((r for r in queries if r["language"] == "portuguese"), key=lambda r: r["query_id"])
    if len(english) < COUNT or len(english) != len(portuguese):
        raise source.InputError("Incomplete English/Portuguese language blocks.")
    by_query = {}
    for row in qrels:
        by_query.setdefault(row["query_id"], []).append(row)

    def signature(identity):
        return sorted(source.canonical({k: v for k, v in row.items() if k != "query_id"})
                      for row in by_query.get(identity, []))

    pairs = []
    for en, pt in zip(english, portuguese):
        if (en["raw_answers"] != pt["raw_answers"] or not signature(en["query_id"])
                or signature(en["query_id"]) != signature(pt["query_id"])):
            raise source.InputError("Translation ordinal is not verified by complete qrels and raw answers.")
        pairs.append((en["query_id"], pt["query_id"]))

    def rank(pair):
        value = SEED + "\0" + DATASET + "\0" + str(pair[0])
        return hashlib.sha256(value.encode("utf-8")).hexdigest(), pair[0]

    pairs.sort(key=rank)
    chosen = pairs[:COUNT]
    return {"version": 1, "dataset": DATASET, "revision": REVISION,
        "seed": SEED, "protocol_sha256": PROTOCOL_SHA256,
        "rule": protocol["query_rule"], "pairing_rule": protocol["pairing_rule"],
        "complete_pdf_corpus": True, "no_adaptive_selection": True,
        "total_independent_original_queries": len(pairs), "selected_independent_original_queries": COUNT,
        "query_texts": COUNT * 2, "pairs": chosen,
        "ranked_original_ids": [pair[0] for pair in pairs],
        "selected_hash_ranks": [{"english_id": pair[0], "portuguese_id": pair[1], "sha256_rank": rank(pair)[0]} for pair in chosen]}


def validate_corpus(documents, pages):
    if len(documents) != 2 or len(pages) != 1360:
        raise source.InputError("The complete source corpus must have two PDFs and 1360 physical pages.")
    if {r["doc_id"] for r in pages} != {r["doc_id"] for r in documents}:
        raise source.InputError("Document/corpus mapping differs.")
    if len({row["corpus_id"] for row in pages}) != len(pages):
        raise source.InputError("Duplicate corpus page IDs.")
    for doc in documents:
        physical = sorted(r["page_number_in_doc"] for r in pages if r["doc_id"] == doc["doc_id"])
        if physical != list(range(doc["page_number"])) or not doc.get("license"):
            raise source.InputError("Physical page map is incomplete or document license absent.")


def validate_pdfs(manifest, destination, fitz):
    physical = []
    for doc in manifest["documents"]:
        with fitz.open(source.safe_path(destination, doc["file"])) as pdf:
            if len(pdf) != doc["page_count"]:
                raise source.InputError(f"Original PDF physical page count differs: {doc['id']}")
            notices = []
            for page in range(min(6, len(pdf))):
                for line in pdf[page].get_text().splitlines():
                    if "creativecommons.org/licenses/by/4.0" in line.lower() or "creative commons" in line.lower():
                        notices.append({"physical_page": page + 1, "line": line.strip()})
            if not notices:
                raise source.InputError("Missing source publisher license notice in front matter.")
            physical.append({"id": doc["id"], "physical_pages": len(pdf), "license_notices": notices})
    return physical


def verify_provenance(observed, frozen):
    # Original dependency versions are historical facts retained byte-for-byte.
    # Different current runtimes cannot change any immutable input field.
    expected = {key: value for key, value in frozen.items() if key != "dependency_versions"}
    actual = {key: value for key, value in observed.items() if key != "dependency_versions"}
    if actual != expected:
        changed = sorted(key for key in set(actual) | set(expected) if actual.get(key) != expected.get(key))
        raise source.InputError(f"Immutable provenance changed during reproduction: {', '.join(changed)}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", required=True, type=Path,
                        help="External cache base; writes vidore_computer_science/REVISION beneath it")
    parser.add_argument("--offline", action="store_true", help="Verify/rebuild solely from local locked inputs; no network")
    args = parser.parse_args(argv)
    cache = args.cache.expanduser().resolve()
    if cache.is_relative_to(REPO):
        raise source.InputError("Choose a cache outside the repository checkout.")
    root = source.safe_path(cache, f"vidore_computer_science/{REVISION}")
    destination = source.safe_path(root, "heldout_v1")
    packaged, protocol, frozen_lock = load_packaged_inputs()
    pq, fitz = load_dependencies()

    # These small official identity projections are bundled so reproduction does
    # not depend on a live viewer continuing to serve this historical revision.
    for filename, relative in (("selection-protocol.json", "selection-protocol.json"),
                               ("hf-tree.json", "sources/hf-tree.json"), ("corpus.json", "sources/corpus.json")):
        retain_locked_file(source.safe_path(root, relative), packaged[filename], PACKAGED_SHA256[filename])

    def fetch(path, base):
        size, sha = PINNED[path]
        target = source.safe_path(base, path)
        data = source.download(DATASET, REVISION, path, target, sha, args.offline)
        if len(data) != size:
            raise source.InputError(f"Source binary length changed: {path}")
        return target

    card_path = source.safe_path(root, "DATASET-CARD.md")
    source.download(DATASET, REVISION, "README.md", card_path, frozen_lock["dataset_card_sha256"], args.offline)
    rows = {}
    for config in ("documents_metadata", "queries", "qrels"):
        file = fetch(f"{config}/test-00000-of-00001.parquet", root / "sources")
        rows[config] = pq.read_table(file).to_pylist()
        normalized = source.canonical(rows[config])
        source.verify(normalized, frozen_lock["annotation_sha256"][config], config)
        target = source.safe_path(root, f"sources/{config}.json")
        retain_locked_file(target, normalized + b"\n", source.digest(normalized + b"\n"))

    selection = select_queries(rows["queries"], rows["qrels"], protocol)
    selection_bytes = source.canonical(selection) + b"\n"
    source.verify(selection_bytes, SELECTION_SHA256, "regenerated selection")
    selected_ids = {identity for pair in selection["pairs"] for identity in pair}
    rows["corpus"] = json.loads(packaged["corpus.json"])
    validate_corpus(rows["documents_metadata"], rows["corpus"])
    annotation_sha = {config: source.digest(source.canonical(values)) for config, values in rows.items()}
    if annotation_sha != frozen_lock["annotation_sha256"]:
        raise source.InputError("Locked normalized annotations changed.")
    entry = {"dataset": DATASET, "revision": REVISION,
        "annotation_sha256": annotation_sha, "languages": ["english", "portuguese"],
        "smoke_document_ids": [doc["doc_id"] for doc in rows["documents_metadata"]],
        "smoke_query_ids": sorted(selected_ids),
        "pdfs": {path: {"sha256": sha, "bytes": size} for path, (size, sha) in PINNED.items() if path.startswith("pdfs/")}}
    manifest = source.vidore_manifest(entry, rows["documents_metadata"], rows["corpus"], rows["queries"], rows["qrels"], "smoke")
    manifest.update(selection="second_domain_heldout_v1", selection_seed=SEED,
        selection_protocol_sha256=PROTOCOL_SHA256, selection_sha256=SELECTION_SHA256,
        complete_pdf_corpus=True, independent_original_query_count=COUNT,
        no_adaptive_selection=True, translations_are_independent_samples=False)
    manifest_bytes = source.canonical(manifest) + b"\n"
    source.verify(manifest_bytes, MANIFEST_SHA256, "regenerated manifest")
    with ThreadPoolExecutor(max_workers=2) as pool:
        list(pool.map(lambda doc: fetch(doc["file"], destination), manifest["documents"]))
    physical = validate_pdfs(manifest, destination, fitz)
    selected_qrels = sum(len(query["qrels"]) for query in manifest["queries"])
    runtime = {"python": sys.version.split()[0], "pyarrow": importlib.import_module("pyarrow").__version__, "pymupdf": fitz.VersionBind}
    observed = {"version": 1, "dataset": DATASET, "revision": REVISION,
        "dataset_url": f"https://huggingface.co/datasets/{DATASET}/tree/{REVISION}",
        "artifact_listing_url": f"https://huggingface.co/api/datasets/{DATASET}/tree/{REVISION}?recursive=true&expand=true",
        "artifact_listing_sha256": source.digest(packaged["hf-tree.json"]),
        "dataset_card_url": f"https://huggingface.co/datasets/{DATASET}/blob/{REVISION}/README.md",
        "dataset_card_sha256": source.digest(card_path.read_bytes()),
        "annotation_license": "CC-BY-4.0", "annotation_sha256": annotation_sha,
        "binary_sources": [{"path": path, "bytes": size, "sha256": sha,
            "source_url": f"https://huggingface.co/datasets/{DATASET}/resolve/{REVISION}/{path}"} for path, (size, sha) in PINNED.items()],
        "projection_note": "Corpus identities and raster dimensions use exact-revision HF viewer JSON; corpus images and supplied OCR markdown are discarded and never downloaded as binaries. Queries/qrels/document metadata use hash-verified original pinned Parquet artifacts.",
        "protocol_sha256": PROTOCOL_SHA256, "selection_sha256": source.digest(selection_bytes),
        "manifest_sha256": source.digest(manifest_bytes),
        "full_corpus_documents": len(manifest["documents"]), "physical_pages": len(manifest["pages"]),
        "full_query_language_counts": dict(sorted(Counter(query["language"] for query in rows["queries"]).items())),
        "selected_query_language_counts": dict(sorted(Counter(query["language"] for query in manifest["queries"]).items())),
        "independent_original_queries": COUNT, "selected_qrel_rows": selected_qrels,
        "distinct_selected_positive_pages": len({row["corpus_id"] for query in manifest["queries"] for row in query["qrels"]}),
        "zero_area_selected_rectangles": manifest["unusable_bounding_box_count"],
        "physical_pdf_validation": physical, "data_bytes": sum(size for size, _ in PINNED.values()),
        "dependency_versions": runtime}
    verify_provenance(observed, frozen_lock)
    retain_locked_file(source.safe_path(destination, "selection.json"), selection_bytes, SELECTION_SHA256)
    retain_locked_file(source.safe_path(destination, "manifest.json"), manifest_bytes, MANIFEST_SHA256)
    retain_locked_file(source.safe_path(root, "provenance.lock.json"), packaged["provenance.lock.json"], PACKAGED_SHA256["provenance.lock.json"])
    source.write_json(source.safe_path(root, "reproduction-runtime.json"), {
        "version": 1, "offline": args.offline, "dependency_versions": runtime,
        "original_dependency_versions": frozen_lock["dependency_versions"],
        "reproducer_sha256": source.digest(Path(__file__).read_bytes()),
        "source_adapter_sha256": source.digest((REPO / "tools/DownloadPdfBenchmarks.py").read_bytes()),
        "manifest_sha256": MANIFEST_SHA256, "selection_sha256": SELECTION_SHA256,
        "protocol_sha256": PROTOCOL_SHA256, "immutable_provenance_identical": True})
    print(f"Panel verified: 2 PDFs, 1360 pages, {COUNT} original pairs / {COUNT * 2} texts, {selected_qrels} positive qrels", flush=True)
    print(f"Manifest: {destination / 'manifest.json'}\nSHA256: {MANIFEST_SHA256}", flush=True)
    print(f"Selection SHA256: {SELECTION_SHA256}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (source.InputError, OSError, KeyError, TypeError, urllib.error.URLError) as error:
        print(f"Benchmark reproduction blocked: {error}", file=sys.stderr)
        sys.exit(1)
