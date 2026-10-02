"""Offline checks for paired comparison summaries, independent of product scorers."""
import importlib.util
import hashlib
import json
import tempfile
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('compare_reports', ROOT / 'tools/ComparePdfBenchmarkReports.py')
compare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compare)


def query(identity, recall):
    return {'id': str(identity), 'language': 'english', 'text': 'unchanged question', 'metrics': [
        {'k': 5, 'recall': recall, 'ndcg': recall, 'reciprocal_rank': recall,
         'evidence_area_coverage': None, 'evidence_region_recall': None}]}


class ComparisonTests(unittest.TestCase):
    def test_identical_results_have_zero_delta_and_zero_uncertainty(self):
        rows = [query(1, .5), query(2, 1)]
        result = compare.retrieval(rows, rows, {})
        metric = result[0]
        self.assertEqual(0, metric['uncertainty']['mean_delta'])
        self.assertEqual([0, 0], metric['uncertainty']['paired_cluster_bootstrap_95_percent'])
        self.assertEqual(2, metric['unchanged'])
        self.assertEqual(0, metric['regressed'])

    def test_translation_pairs_are_resampled_as_one_cluster(self):
        result = compare.intervals({'en1': 1, 'pt1': 1, 'en2': -1, 'pt2': -1},
                                   {'en1': 'pair1', 'pt1': 'pair1', 'en2': 'pair2', 'pt2': 'pair2'}, repetitions=100)
        self.assertEqual(2, result['clusters'])
        self.assertEqual(0, result['mean_delta'])

    def test_changed_query_selection_is_rejected(self):
        with self.assertRaises(ValueError):
            compare.retrieval([query(1, 0)], [query(2, 1)], {})

    def test_geometry_without_annotations_stays_unscored(self):
        result = compare.retrieval([query(1, 0)], [query(1, 1)], {})
        self.assertEqual(0, result[-1]['query_count'])
        self.assertIsNone(result[-1]['uncertainty'])
        self.assertIsNone(result[-1]['after_mean'])

    def test_policy_selection_is_explicit_and_does_not_collapse_duplicate_ids(self):
        rank = dict(query(1, .5), preview_policy='rank')
        diverse = dict(query(1, 1), preview_policy='diverse')
        report = {'primary_preview_policy': 'rank', 'queries': [rank, diverse]}
        policy, rows = compare.policy_rows(report, None)
        self.assertEqual('rank', policy)
        self.assertEqual([rank], rows)
        self.assertEqual(('diverse', [diverse]), compare.policy_rows(report, 'diverse'))
        with self.assertRaises(ValueError):
            compare.retrieval(report['queries'], report['queries'], {})

    def test_absent_policy_and_duplicate_rows_within_policy_are_rejected(self):
        rank = dict(query(1, .5), preview_policy='rank')
        with self.assertRaises(ValueError):
            compare.policy_rows({'queries': [rank]}, 'diverse')
        with self.assertRaises(ValueError):
            compare.policy_rows({'queries': [rank, rank]}, 'rank')

    def test_model_changes_require_explicit_flag_and_keep_precision_dimensions(self):
        before = {'selected_model': 'Granite97M', 'embedding_policy': {'precision': 'fp32', 'dimensions': 384}}
        after = {'selected_model': 'Granite311M', 'embedding_policy': {'precision': 'fp32', 'dimensions': 384}}
        with self.assertRaises(ValueError):
            compare.validate_configuration(before, after)
        changes = compare.validate_configuration(before, after, allow_model_change=True)
        self.assertEqual('Granite311M', changes['selected_model']['after'])
        self.assertIn('embedding_policy', changes)
        for name, value in [('precision', 'int8'), ('dimensions', 768)]:
            changed = dict(after, embedding_policy=dict(after['embedding_policy'], **{name: value}))
            with self.assertRaises(ValueError):
                compare.validate_configuration(before, changed, allow_model_change=True)

    def test_controlled_flags_do_not_relax_manifest_or_budget_equality(self):
        before = {'selected_model': 'a', 'candidate_limit': 100}
        after = {'selected_model': 'b', 'candidate_limit': 200}
        with self.assertRaises(ValueError):
            compare.validate_configuration(before, after, allow_model_change=True)

    def test_stable_document_fixture_protocol_and_mapping_must_match(self):
        mapping = [{'original_id': 'one', 'source_sha256': 'sha', 'document_id': 'fixed-guid'}]
        before = {'document_identity_protocol': 'source-document-guid-v1', 'document_identity_map': mapping}
        self.assertEqual({}, compare.validate_configuration(before, before))
        for after in [{}, dict(before, document_identity_map=None),
                      dict(before, document_identity_map=mapping + mapping),
                      dict(before, document_identity_map=[dict(mapping[0], document_id='other-guid')]),
                      dict(before, document_identity_map=[dict(mapping[0], source_sha256='other-hash')])]:
            with self.assertRaises(ValueError):
                compare.validate_configuration(before, after, allow_model_change=True)

    def test_verified_full_partition_manifest_requires_identical_entire_corpus_and_qrels(self):
        base = {'dataset': 'pinned', 'revision': 'revision', 'coordinates_frame': 'imageNormalizedTopLeft',
                'documents': [{'id': 'one', 'sha256': 'hash', 'page_count': 2}],
                'pages': [{'corpus_id': 1, 'page_number': 1}, {'corpus_id': 2, 'page_number': 2}],
                'queries': [{'id': '1', 'text': 'same', 'qrels': [{'corpus_id': 2, 'score': 2}]},
                            {'id': '2', 'text': 'other', 'qrels': [{'corpus_id': 1, 'score': 1}]}]}
        subset = dict(base, queries=base['queries'][:1])
        with tempfile.TemporaryDirectory() as temp:
            first, second = Path(temp) / 'full.json', Path(temp) / 'dev.json'
            def save(value, path):
                raw = json.dumps(value).encode(); path.write_bytes(raw)
                return {'manifest_sha256': hashlib.sha256(raw).hexdigest()}
            before, after = save(base, first), save(subset, second)
            result = compare.validate_manifest_selection(before, after, first, second)
            self.assertTrue(result['identical_complete_corpus_verified'])
            with self.assertRaises(ValueError):
                compare.validate_configuration(before, after)
            self.assertIn('manifest_sha256', compare.validate_configuration(before, after, verified_manifest_selection=True))
            for changed in [dict(subset, pages=subset['pages'][:1]),
                            dict(subset, queries=[dict(subset['queries'][0], qrels=[{'corpus_id': 2, 'score': 1}])])]:
                changed_report = save(changed, second)
                with self.assertRaises(ValueError):
                    compare.validate_manifest_selection(before, changed_report, first, second)
            with self.assertRaises(ValueError):
                compare.validate_manifest_selection(before, {'manifest_sha256': 'wrong'}, first, second)


if __name__ == '__main__':
    unittest.main()
