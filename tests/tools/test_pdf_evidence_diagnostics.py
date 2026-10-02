import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]

def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tools' / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

export = module('export_passages', 'ExportVerifiedBenchmarkPassages.py')
ties = module('tie_diagnostics', 'AuditPdfTieSensitivity.py')


class EvidenceDiagnosticTests(unittest.TestCase):
    def test_utf16_excerpt_offsets_are_not_python_codepoint_offsets(self):
        self.assertEqual('evidence', export.utf16_slice('😀 evidence tail', 3, 8))
        with self.assertRaises(ValueError):
            export.utf16_slice('short', 4, 2)

    def test_raw_vector_tie_risk_is_separate_from_fused_score_ties(self):
        evidence = [{'passage_id': str(i), 'original_document_id': 'doc', 'page_number': i + 1,
                     'fused_score': 10 - i, 'semantic_rank': i + 1, 'excerpt': 'short'} for i in range(10)]
        row = {'id': 'q', 'language': 'english', 'exposed_evidence': evidence}
        groups = {'9': [('9', 'doc', 10), ('hidden', 'doc', 11)]}
        result = ties.audit(row, groups)
        self.assertEqual(0, result['exposed_fused_score_tie_groups'])
        self.assertEqual(1, result['exposed_passages_in_byte_identical_vector_groups'])
        self.assertEqual([11], result['potential_tied_preview_cap_boundaries'][0]['pages'])


if __name__ == '__main__':
    unittest.main()
