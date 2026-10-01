"""Offline regression tests for the PDF dataset adapter (no third-party packages)."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

source = Path(__file__).resolve().parents[2] / "tools" / "DownloadPdfBenchmarks.py"
spec = importlib.util.spec_from_file_location("download_pdf_benchmarks", source)
download = importlib.util.module_from_spec(spec)
spec.loader.exec_module(download)


class PdfDatasetAdapterTests(unittest.TestCase):
    def test_cache_paths_reject_escape_and_windows_absolute_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in ["../outside.pdf", "/outside.pdf", "C:/outside.pdf", "a\\b.pdf"]:
                with self.subTest(relative=relative), self.assertRaises(download.InputError):
                    download.safe_path(root, relative)
            self.assertEqual(download.safe_path(root, "pdfs/a.pdf"), root / "pdfs" / "a.pdf")

    def test_corrupt_cached_file_fails_without_network(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "a.pdf"
            path.write_bytes(b"corrupt")
            with patch.object(download, "request") as request:
                with self.assertRaises(download.InputError):
                    download.download("example", "revision", "a.pdf", path, "0" * 64, offline=True)
                request.assert_not_called()

    def test_viewer_revision_mismatch_rejects_annotation_batch(self):
        class Response:
            headers = {"x-revision": "unexpected"}
            def __enter__(self): return self
            def __exit__(self, *_): pass
            def read(self): return b"{}"
        with patch.object(download.urllib.request, "urlopen", return_value=Response()):
            with self.assertRaises(download.InputError):
                download.request("https://datasets-server.huggingface.co/rows", revision="locked")

    def test_corpus_normalization_omits_ocr_and_expiring_image_urls(self):
        row = {"corpus_id": 4, "doc_id": "a", "page_number_in_doc": 0,
               "image": {"width": 100, "height": 200, "src": "expiring-url"}, "markdown": "OCR, not gold"}
        actual = download.normalized_row("corpus", row)
        self.assertEqual(actual, {"corpus_id": 4, "doc_id": "a", "page_number_in_doc": 0, "width": 100, "height": 200})

    def test_boxes_are_normalized_using_original_page_image_dimensions(self):
        actual = download.normalized_box({"x1": 25, "y1": 50, "x2": 75, "y2": 150}, {"width": 100, "height": 200})
        self.assertEqual(actual, [0.25, 0.25, 0.75, 0.75])
        with self.assertRaises(download.InputError):
            download.normalized_box({"x1": 0, "y1": 0, "x2": 101, "y2": 10}, {"width": 100, "height": 200})

    @staticmethod
    def inputs():
        entry = {"dataset": "vidore/vidore_v3_hr", "revision": "locked", "annotation_sha256": {},
                 "languages": ["english"], "smoke_document_ids": ["a"], "smoke_query_ids": [7],
                 "pdfs": {"pdfs/a.pdf": {"sha256": "1" * 64}}}
        documents = [{"doc_id": "a", "file_name": "a.pdf", "license": "cc-by-4.0", "page_number": 2, "url": "publisher"},
                     {"doc_id": "b", "file_name": "b.pdf", "license": "cc-by-4.0", "page_number": 1, "url": "publisher"}]
        pages = [{"corpus_id": 4, "doc_id": "a", "page_number_in_doc": 1, "width": 100, "height": 200},
                 {"corpus_id": 5, "doc_id": "b", "page_number_in_doc": 0, "width": 100, "height": 200}]
        queries = [{"query_id": 7, "query": "Question?", "language": "english"}]
        qrels = [{"query_id": 7, "corpus_id": 4, "score": 2, "bounding_boxes": []}]
        return entry, documents, pages, queries, qrels

    def test_smoke_selection_keeps_all_positive_labels_and_one_based_pages(self):
        actual = download.vidore_manifest(*self.inputs(), subset="smoke")
        self.assertEqual(actual["pages"][0]["page_number"], 2)
        self.assertEqual(actual["queries"][0]["qrels"][0]["score"], 2)
        self.assertFalse(actual["supplied_ocr_markdown_used"])

    def test_smoke_query_with_positive_outside_subset_fails_instead_of_dropping_label(self):
        entry, documents, pages, queries, qrels = self.inputs()
        qrels.append({"query_id": 7, "corpus_id": 5, "score": 1, "bounding_boxes": []})
        with self.assertRaises(download.InputError):
            download.vidore_manifest(entry, documents, pages, queries, qrels, "smoke")

    def test_missing_publisher_license_is_not_silently_inherited_from_annotations(self):
        entry, documents, pages, queries, qrels = self.inputs()
        documents[0]["license"] = ""
        with self.assertRaises(download.InputError):
            download.vidore_manifest(entry, documents, pages, queries, qrels, "smoke")

    def test_full_olm_corpus_rejects_corrupt_non_smoke_pdf_without_overwriting_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            annotations = download.canonical({"pdf": "category/full-only.pdf", "page": 1, "id": "check", "type": "present", "text": "gold"}) + b"\n"
            source = cache / "sources/bench_data/category.jsonl"
            source.parent.mkdir(parents=True)
            source.write_bytes(annotations)
            pdf = cache / "full/pdfs/category/full-only.pdf"
            pdf.parent.mkdir(parents=True)
            pdf.write_bytes(b"corrupt")
            manifest = cache / "full/manifest.json"
            manifest.write_bytes(b"preserve")
            entry = {"dataset": "allenai/olmOCR-bench", "revision": "pinned", "evaluator_revision": "code",
                     "annotations": [{"path": "bench_data/category.jsonl", "category": "category", "sha256": download.digest(annotations)}],
                     "pdf_sha256": {"category/full-only.pdf": "0" * 64}, "smoke_pdf_ids": []}
            with patch.object(download, "request") as request:
                with self.assertRaises(download.InputError):
                    download.prepare_olmocr(entry, cache, "full", offline=True)
                request.assert_not_called()
            self.assertEqual(manifest.read_bytes(), b"preserve")

    def test_zero_area_boxes_are_explicit_diagnostics_and_keep_page_relevance(self):
        entry, documents, pages, queries, qrels = self.inputs()
        original = {"annotator": 0, "x1": 25, "x2": 25, "y1": 50, "y2": 150}
        qrels[0]["bounding_boxes"] = [original]
        actual = download.vidore_manifest(entry, documents, pages, queries, qrels, "smoke")
        self.assertEqual(actual["unusable_bounding_box_count"], 1)
        self.assertEqual(actual["bbox_diagnostics"][0]["original_pixel_box"], original)
        self.assertEqual(actual["queries"][0]["qrels"][0]["score"], 2)
        self.assertEqual(actual["queries"][0]["qrels"][0]["bounding_boxes"], [])
        self.assertTrue(actual["annotation_warnings"])


if __name__ == "__main__":
    unittest.main()
