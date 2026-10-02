"""Deterministic, identity-only held-out sampling; all tests stay offline."""
import importlib.util
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("pdf_partitions", ROOT / "tools/PreparePdfEvaluationPartitions.py")
partition = importlib.util.module_from_spec(spec)
spec.loader.exec_module(partition)


class PartitionTests(unittest.TestCase):
    def test_selection_is_disjoint_excludes_smoke_and_ignores_order_and_answers(self):
        rows = [{"pdf": f"category/{i:02d}.pdf", "answer": "ignored", "type": "present"} for i in range(30)]
        first = partition.select_olmocr({"category": rows}, ["category/00.pdf"])
        reversed_rows = [dict(row, answer="changed", text="extraction cannot affect selection") for row in reversed(rows)]
        self.assertEqual(first, partition.select_olmocr({"category": reversed_rows}, ["category/00.pdf"]))
        dev, held = first["development"]["category"], first["heldout"]["category"]
        self.assertEqual(5, len(dev))
        self.assertEqual(10, len(held))
        self.assertFalse(set(dev) & set(held))
        self.assertNotIn("category/00.pdf", dev + held)

    def test_duplicate_annotation_rows_do_not_change_document_sampling(self):
        rows = [{"pdf": f"{i}.pdf"} for i in range(20)]
        self.assertEqual(partition.select_olmocr({"a": rows}, []), partition.select_olmocr({"a": rows * 3}, []))

    def test_small_categories_fail_instead_of_silently_shrinking_holdout(self):
        with self.assertRaises(partition.source.InputError):
            partition.select_olmocr({"a": [{"pdf": f"{i}.pdf"} for i in range(14)]}, [])

    def test_second_panel_uses_unseen_subsequent_hash_ranks(self):
        rows = [{"pdf": f"a/{i}.pdf"} for i in range(35)]
        first = partition.select_olmocr({"a": rows}, [])
        second = partition.select_olmocr({"a": rows}, [], heldout_round=2)
        self.assertEqual(first['development'], second['development'])
        self.assertEqual(10, len(second['heldout_v2']['a']))
        self.assertFalse(set(second['heldout_v2']['a']) & set(first['heldout']['a'] + first['development']['a']))

    def test_translation_pairs_stay_together_and_keep_complete_corpus_selection(self):
        queries, qrels = [], []
        for identity in range(7):
            for offset, language in ((0, "english"), (100, "portuguese")):
                queries.append({"query_id": identity + offset, "language": language, "raw_answers": [str(identity)]})
                qrels.append({"query_id": identity + offset, "corpus_id": identity, "score": 2})
        partitions = partition.select_vidore(list(reversed(queries)), qrels, [0, 100])
        self.assertEqual(2, len(partitions["development"]))
        self.assertEqual(4, len(partitions["heldout"]))
        pairs = partitions["development"] + partitions["heldout"]
        self.assertTrue(all(second - first == 100 for first, second in pairs))
        self.assertNotIn((0, 100), pairs)
        self.assertFalse(set(partitions["development"]) & set(partitions["heldout"]))

    def test_mismatched_translation_relevance_and_split_smoke_pairs_are_rejected(self):
        queries = [{"query_id": 0, "language": "english", "raw_answers": ["answer"]},
                   {"query_id": 100, "language": "portuguese", "raw_answers": ["answer"]}]
        qrels = [{"query_id": 0, "corpus_id": 0, "score": 2}, {"query_id": 100, "corpus_id": 1, "score": 2}]
        with self.assertRaises(partition.source.InputError):
            partition.select_vidore(queries, qrels, [])
        qrels[1]["corpus_id"] = 0
        with self.assertRaises(partition.source.InputError):
            partition.select_vidore(queries, qrels, [0])


if __name__ == "__main__":
    unittest.main()
