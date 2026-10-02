"""Offline bounded checks for the Linux resource wrapper, without any models."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

TOOL = Path(__file__).resolve().parents[2] / 'tools/RunMeasuredBenchmark.py'


@unittest.skipUnless(sys.platform == 'linux', 'Linux resource units are explicit')
class MeasurementTests(unittest.TestCase):
    def test_optional_progress_finishes_with_authoritative_report_link(self):
        with tempfile.TemporaryDirectory() as directory:
            report, progress = Path(directory) / 'report.json', Path(directory) / 'progress.json'
            result = subprocess.run([sys.executable, str(TOOL), '--output', str(report), '--progress-output',
                                     str(progress), '--', sys.executable, '-c',
                                     'import time; time.sleep(.05)'], capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            last = json.loads(progress.read_text())
            self.assertEqual('completed', last['status'])
            self.assertEqual(str(report), last['final_resource_report'])
            self.assertEqual(json.loads(report.read_text())['peak_rss_kib'], last['peak_rss_kib'])

    def test_success_and_failure_are_recorded_and_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'resources.json'
            command = [sys.executable, str(TOOL), '--output', str(output), '--', sys.executable,
                       '-c', 'buffer=bytearray(2_000_000); print(len(buffer))']
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads(output.read_text())
            self.assertEqual(0, report['return_code'])
            self.assertGreater(report['peak_rss_kib'], 0)
            self.assertGreater(report['elapsed_seconds'], 0)
            self.assertAlmostEqual(report['total_cpu_seconds'], report['user_cpu_seconds'] + report['system_cpu_seconds'])
            original = output.read_bytes()
            self.assertNotEqual(0, subprocess.run(command, capture_output=True).returncode)
            self.assertEqual(original, output.read_bytes())
            failed = Path(directory) / 'failed.json'
            result = subprocess.run([sys.executable, str(TOOL), '--output', str(failed), '--',
                                     sys.executable, '-c', 'raise SystemExit(7)'], capture_output=True)
            self.assertEqual(7, result.returncode)
            self.assertEqual(7, json.loads(failed.read_text())['return_code'])


if __name__ == '__main__':
    unittest.main()
