"""Prepare deterministic development/held-out PDF benchmark partitions.

Selections use only pinned identities, never extracted text, answers or scores.
Every selected olmOCR PDF retains all upstream checks. ViDoRe retains the full
PDF/page corpus in both partitions and groups verified translations by language ordinal.
Cached cards, original PDF licenses and relevance labels are retained unchanged.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import shutil
import sys
import DownloadPdfBenchmarks as source

SEED = "ContextMole-heldout-v1"


def rank_key(category, identity):
    return hashlib.sha256((SEED + "\0" + category + "\0" + str(identity)).encode("utf-8")).hexdigest(), str(identity)


def select_olmocr(rows_by_category, smoke_ids, heldout_round=1):
    smoke = set(smoke_ids)
    heldout_name = "heldout" if heldout_round == 1 else f"heldout_v{heldout_round}"
    start, end = 5 + 10 * (heldout_round - 1), 5 + 10 * heldout_round
    selected = {"development": {}, heldout_name: {}}
    for category, rows in sorted(rows_by_category.items()):
        ids = sorted({row["pdf"] for row in rows} - smoke, key=lambda identity: rank_key(category, identity))
        if len(ids) < end:
            raise source.InputError(f"Not enough non-smoke PDFs in {category} for held-out round {heldout_round}.")
        selected["development"][category] = ids[:5]
        selected[heldout_name][category] = ids[start:end]
    return selected


def fetch_document(entry, root, destination, relative, expected, offline):
    target = source.safe_path(destination, relative)
    if not target.exists():
        for subset in ("full", "smoke", "development", "heldout"):
            cached = source.safe_path(root / subset, relative)
            if cached.exists() and cached != target:
                source.verify(cached.read_bytes(), expected, str(cached))
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(cached, target)
                break
    return source.download(entry["dataset"], entry["revision"],
                           "bench_data/pdfs/" + relative.removeprefix("pdfs/") if entry["dataset"] == "allenai/olmOCR-bench" else relative,
                           target, expected, offline)


def prepare_olmocr(entry, root, offline, workers, heldout_round=1):
    rows_by_category = {}
    for item in entry["annotations"]:
        data = source.download(entry["dataset"], entry["revision"], item["path"],
                               source.safe_path(root / "sources", item["path"]), item["sha256"], offline)
        rows_by_category[item["category"]] = [json.loads(line) for line in data.splitlines() if line.strip()]
    partitions = select_olmocr(rows_by_category, entry["smoke_pdf_ids"], heldout_round)
    for name, categories in partitions.items():
        destination = root / name
        checks, documents = [], []
        for category, ids in categories.items():
            checks.extend(row for row in rows_by_category[category] if row["pdf"] in set(ids))
            documents.extend({"id": identity, "file": "pdfs/" + identity,
                              "sha256": entry["pdf_sha256"][identity], "category": category}
                             for identity in ids)
        documents.sort(key=lambda doc: doc["id"])
        # Freeze identity selection before downloading, extracting, or inspecting any candidate.
        source.write_json(destination / "selection.json", {"seed": SEED, "heldout_round": heldout_round,
            "rule": "SHA256(seed + NUL + category + NUL + PDF ID), ascending; smoke excluded; first 5 development; heldout round r uses ranks [5 + 10*(r-1), 5 + 10*r)", "categories": categories})
        def fetch(doc):
            fetch_document(entry, root, destination, doc["file"], doc["sha256"], offline)
        with ThreadPoolExecutor(max_workers=workers) as pool:
            list(pool.map(fetch, documents))
        data = b"".join(source.canonical(row) + b"\n" for row in checks)
        (destination / "checks.jsonl").write_bytes(data)
        source.write_json(destination / "manifest.json", {
            "version": 1, "dataset": entry["dataset"], "revision": entry["revision"], "selection": name,
            "license": "ODC-BY-1.0", "documents": documents, "checks_file": "checks.jsonl", "checks_sha256": source.digest(data),
            "upstream_evaluator_revision": entry["evaluator_revision"], "selection_seed": SEED,
            "smoke_excluded": True, "complete_upstream_checks_per_pdf": True})
        print(f"olmOCR {name}: {len(documents)} PDFs, {len(checks)} supplied checks", flush=True)
    return partitions


def select_vidore(queries, qrels, smoke_ids):
    english = sorted((row for row in queries if row["language"] == "english"), key=lambda row: row["query_id"])
    portuguese = sorted((row for row in queries if row["language"] == "portuguese"), key=lambda row: row["query_id"])
    if not english or len(english) != len(portuguese):
        raise source.InputError("The pinned English/Portuguese translation groups must be complete.")
    by_query = {}
    for row in qrels:
        by_query.setdefault(row["query_id"], []).append(row)
    pairs = []
    smoke = set(smoke_ids)
    def relevance(identity):
        return sorted((row["corpus_id"], row["score"]) for row in by_query.get(identity, []))
    for original, translation in zip(english, portuguese):
        # The pinned card orders languages in blocks, including a second numerical-query block.
        # Validate positional pairing with preserved original answers and every page relevance label.
        if original.get("raw_answers") != translation.get("raw_answers") or relevance(original["query_id"]) != relevance(translation["query_id"]):
            raise source.InputError("English/Portuguese language ordinal is not a verified translation pair.")
        if (original["query_id"] in smoke) != (translation["query_id"] in smoke):
            raise source.InputError("Smoke selection splits a translation pair.")
        if original["query_id"] not in smoke:
            pairs.append((original["query_id"], translation["query_id"]))
    pairs.sort(key=lambda pair: rank_key("vidore_query_pair", pair[0]))
    split = len(pairs) // 3
    return {"development": pairs[:split], "heldout": pairs[split:]}


def prepare_vidore(entry, root, offline, workers):
    data = {config: source.viewer_rows(entry, config, root, offline) for config in
            ("documents_metadata", "corpus", "queries", "qrels")}
    complete = source.vidore_manifest(entry, data["documents_metadata"], data["corpus"], data["queries"], data["qrels"], "full")
    partitions = select_vidore(data["queries"], data["qrels"], entry["smoke_query_ids"])
    for name, pairs in partitions.items():
        destination = root / name
        source.write_json(destination / "selection.json", {"seed": SEED,
            "rule": "Verify English/Portuguese translation pairs by language ordinal, retained raw answers and full relevance set; exclude smoke pairs; SHA256(seed + NUL + vidore_query_pair + NUL + English query ID), ascending; first third development, remainder heldout",
            "pairs": pairs})
        ids = {identity for pair in pairs for identity in pair}
        manifest = dict(complete, selection=name, selection_seed=SEED,
                        smoke_excluded=True, complete_pdf_corpus=True,
                        queries=[query for query in complete["queries"] if int(query["id"]) in ids])
        def fetch(doc):
            fetch_document(entry, root, destination, doc["file"], doc["sha256"], offline)
        with ThreadPoolExecutor(max_workers=workers) as pool:
            list(pool.map(fetch, manifest["documents"]))
        source.write_json(destination / "manifest.json", manifest)
        print(f"ViDoRe {name}: {len(manifest['documents'])} PDFs, {len(manifest['pages'])} pages, {len(manifest['queries'])} queries", flush=True)
    return partitions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--dataset", choices=("olmocr", "vidore", "all"), default="all")
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--heldout-round", type=int, choices=(1, 2, 3), default=1,
                        help="olmOCR: use fresh subsequent identity ranks after a previous held-out panel is inspected")
    args = parser.parse_args()
    if args.heldout_round != 1 and args.dataset != "olmocr":
        raise source.InputError("Subsequent held-out rounds require --dataset olmocr; ViDoRe split is unchanged.")
    repo = Path(__file__).resolve().parent.parent
    if args.cache.resolve().is_relative_to(repo) or not 1 <= args.workers <= 8:
        raise source.InputError("Choose an external cache and 1-8 download workers.")
    lock = json.loads((repo / "benchmarks/pdf/datasets.lock.json").read_text())
    for name, prepare in (("olmocr", prepare_olmocr), ("vidore", prepare_vidore)):
        if args.dataset not in (name, "all"):
            continue
        entry = lock[name]
        root = args.cache.resolve() / name / entry["revision"]
        if not args.offline:
            source.download(entry["dataset"], entry["revision"], "README.md", root / "DATASET-CARD.md", entry["card_sha256"])
        if name == "olmocr":
            prepare(entry, root, args.offline, args.workers, args.heldout_round)
        else:
            prepare(entry, root, args.offline, args.workers)


if __name__ == "__main__":
    try:
        main()
    except (source.InputError, OSError, KeyError, source.urllib.error.URLError) as error:
        print(f"Benchmark partition error: {error}", file=sys.stderr)
        sys.exit(1)
