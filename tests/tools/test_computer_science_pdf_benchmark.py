"""Offline checks for the frozen, portable Computer Science panel reproducer."""
import copy
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/PrepareComputerSciencePdfBenchmark.py"
spec = importlib.util.spec_from_file_location("computer_science_panel", SCRIPT)
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class ComputerSciencePanelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.packaged, cls.protocol, cls.lock = panel.load_packaged_inputs()

    def paired_rows(self, count=60):
        queries, qrels = [], []
        for original in range(count):
            for offset, language in ((0, "english"), (1000, "portuguese")):
                identity = original + offset
                queries.append({"query_id": identity, "language": language,
                                "query": f"text {identity}", "raw_answers": [str(original)]})
                qrels.append({"query_id": identity, "corpus_id": original, "score": 2,
                              "content_type": ["text"],
                              "bounding_boxes": [{"x1": 1, "y1": 2, "x2": 3, "y2": 4}]})
        return queries, qrels

    def test_all_checked_in_input_bytes_match_original_locks(self):
        for name, expected in panel.PACKAGED_SHA256.items():
            self.assertEqual(expected, panel.source.digest(self.packaged[name]), name)
        self.assertEqual(panel.MANIFEST_SHA256, self.lock["manifest_sha256"])
        self.assertEqual(panel.SELECTION_SHA256, self.lock["selection_sha256"])

    def test_selection_lock_has_48_original_clusters_and_96_texts(self):
        selection = json.loads(self.packaged["selection.json"])
        self.assertEqual(48, len(selection["pairs"]))
        self.assertEqual(96, len({identity for pair in selection["pairs"] for identity in pair}))
        self.assertEqual(215, len(selection["ranked_original_ids"]))
        self.assertEqual(selection["ranked_original_ids"][:48], [pair[0] for pair in selection["pairs"]])

    def test_corpus_projection_contains_only_public_identities_and_dimensions(self):
        pages = json.loads(self.packaged["corpus.json"])
        self.assertEqual(1360, len(pages))
        fields = {"corpus_id", "doc_id", "page_number_in_doc", "width", "height"}
        self.assertTrue(all(set(page) == fields for page in pages))
        self.assertEqual(self.lock["annotation_sha256"]["corpus"], panel.source.digest(panel.source.canonical(pages)))

    def test_changed_packaged_metadata_never_relocks(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for name, data in self.packaged.items():
                (directory / name).write_bytes(data)
            for name, data in self.packaged.items():
                with self.subTest(name=name):
                    (directory / name).write_bytes(data + b" ")
                    with self.assertRaisesRegex(panel.source.InputError, "SHA-256 mismatch"):
                        panel.load_packaged_inputs(directory)
                    (directory / name).write_bytes(data)

    def test_query_order_text_and_equal_answers_cannot_change_selected_identities(self):
        queries, qrels = self.paired_rows()
        original = panel.select_queries(queries, qrels, self.protocol)
        changed = [dict(row, query="Different apparent difficulty", raw_answers=["same replacement"])
                   for row in reversed(queries)]
        self.assertEqual(original, panel.select_queries(changed, list(reversed(qrels)), self.protocol))
        self.assertEqual(48, len(original["pairs"]))
        self.assertTrue(all(pt - en == 1000 for en, pt in original["pairs"]))

    def test_translation_checks_include_every_qrel_field_and_box(self):
        queries, qrels = self.paired_rows()
        for field, value in (("corpus_id", -1), ("score", 1), ("content_type", ["figure"]),
                             ("bounding_boxes", [{"x1": 1, "y1": 2, "x2": 4, "y2": 4}])):
            with self.subTest(field=field):
                changed = copy.deepcopy(qrels)
                changed[1][field] = value
                with self.assertRaisesRegex(panel.source.InputError, "Translation ordinal"):
                    panel.select_queries(queries, changed, self.protocol)

    def test_pair_requires_equal_raw_answers_and_nonempty_complete_qrels(self):
        queries, qrels = self.paired_rows()
        queries[1]["raw_answers"] = ["different answer"]
        with self.assertRaisesRegex(panel.source.InputError, "Translation ordinal"):
            panel.select_queries(queries, qrels, self.protocol)
        queries, qrels = self.paired_rows()
        with self.assertRaisesRegex(panel.source.InputError, "Translation ordinal"):
            panel.select_queries(queries, qrels[2:], self.protocol)

    def test_duplicate_ids_and_incomplete_language_blocks_fail(self):
        queries, qrels = self.paired_rows()
        with self.assertRaisesRegex(panel.source.InputError, "Duplicate"):
            panel.select_queries(queries + [queries[0]], qrels, self.protocol)
        with self.assertRaisesRegex(panel.source.InputError, "Incomplete"):
            panel.select_queries(queries[:-1], qrels, self.protocol)
        queries, qrels = self.paired_rows(47)
        with self.assertRaisesRegex(panel.source.InputError, "Incomplete"):
            panel.select_queries(queries, qrels, self.protocol)

    def test_complete_corpus_page_map_required(self):
        pages = json.loads(self.packaged["corpus.json"])
        documents = [{"doc_id": doc["id"], "page_number": doc["physical_pages"], "license": "CC-BY-4.0"}
                     for doc in self.lock["physical_pdf_validation"]]
        panel.validate_corpus(documents, pages)
        with self.assertRaisesRegex(panel.source.InputError, "1360"):
            panel.validate_corpus(documents, pages[:-1])
        duplicated = pages[:-1] + [pages[0]]
        with self.assertRaisesRegex(panel.source.InputError, "Duplicate"):
            panel.validate_corpus(documents, duplicated)
        changed = copy.deepcopy(pages)
        changed[0]["page_number_in_doc"] = 1
        with self.assertRaisesRegex(panel.source.InputError, "page map"):
            panel.validate_corpus(documents, changed)

    def test_runtime_versions_are_separate_but_every_input_field_remains_locked(self):
        observed = copy.deepcopy(self.lock)
        observed["dependency_versions"] = {"python": "other", "pyarrow": "other", "pymupdf": "other"}
        panel.verify_provenance(observed, self.lock)
        for field in ("manifest_sha256", "selection_sha256", "annotation_sha256", "physical_pdf_validation"):
            with self.subTest(field=field):
                changed = copy.deepcopy(observed)
                changed[field] = "changed"
                with self.assertRaisesRegex(panel.source.InputError, field):
                    panel.verify_provenance(changed, self.lock)

    def test_identical_cached_input_is_read_only_and_changed_input_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "input.json"
            data = b"frozen\n"
            expected = panel.source.digest(data)
            panel.retain_locked_file(path, data, expected)
            os.utime(path, ns=(1_000_000_000, 1_000_000_000))
            panel.retain_locked_file(path, data, expected)
            self.assertEqual(1_000_000_000, path.stat().st_mtime_ns)
            path.write_bytes(b"changed")
            with self.assertRaisesRegex(panel.source.InputError, "SHA-256 mismatch"):
                panel.retain_locked_file(path, data, expected)
            self.assertEqual(b"changed", path.read_bytes())

    def test_missing_dependencies_have_clear_optional_package_guidance(self):
        with patch.object(panel.importlib, "import_module", side_effect=ModuleNotFoundError("No module named pyarrow")):
            with self.assertRaisesRegex(panel.source.InputError, "pyarrow==21.0.0.*PyMuPDF==1.26.6"):
                panel.load_dependencies()

    def test_explicit_external_cache_is_required_before_dependencies(self):
        with self.assertRaisesRegex(panel.source.InputError, "outside the repository"):
            panel.main(["--cache", str(ROOT / "benchmarks"), "--offline"])
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                panel.main([])
        self.assertEqual(2, error.exception.code)

    def test_help_discovers_repo_from_script_and_needs_no_optional_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = subprocess.run([sys.executable, "-S", str(SCRIPT), "--help"],
                                    cwd=temporary, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("--cache", result.stdout)
        self.assertIn("--offline", result.stdout)


if __name__ == "__main__":
    unittest.main()
