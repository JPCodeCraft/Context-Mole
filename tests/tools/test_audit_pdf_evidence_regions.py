import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('regions', ROOT / 'tools/AuditPdfEvidenceRegions.py')
regions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(regions)


class RegionAuditTests(unittest.TestCase):
    def test_overlapping_rectangles_are_unioned(self):
        self.assertAlmostEqual(.8, regions.union_area([[0, 0, .6, 1], [.2, 0, .8, 1]]))

    def test_precision_distinguishes_broad_source_box_from_full_gold_recall(self):
        query = {'qrels': [{'corpus_id': 1, 'bounding_boxes': [[0, 0, .5, 1]]}]}
        row = {'ranked_pages': [{'document_id': 'doc', 'page_number': 1}], 'exposed_evidence': [
            {'original_document_id': 'doc', 'page_number': 1, 'literal_citation_valid': True,
             'location': {'region': {'x': 0, 'y': 0, 'width': 1, 'height': 1}}}],
               'metrics': [{'k': 1, 'evidence_area_coverage': 1}]}
        metric = regions.metric(query, {1: ('doc', 1)}, row, 1)
        self.assertEqual(1, metric['existing_evidence_area_coverage_reproduced'])
        self.assertEqual(.5, metric['source_box_annotation_precision_relevant_top_k_pages'])
        self.assertEqual(.5, metric['unannotated_fraction_of_predicted_area'])

    def test_projection_disagreement_is_rejected(self):
        query = {'qrels': [{'corpus_id': 1, 'bounding_boxes': [[0, 0, .5, 1]]}]}
        row = {'ranked_pages': [], 'exposed_evidence': [], 'metrics': [{'k': 1, 'evidence_area_coverage': 1}]}
        with self.assertRaises(ValueError):
            regions.metric(query, {1: ('doc', 1)}, row, 1)


if __name__ == '__main__':
    unittest.main()
