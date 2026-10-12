#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('report', Path(__file__).with_name('read-metal-report.py'))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


def fixture(values):
    return '<script type="application/json" id="frameintervaldata">'+json.dumps({'frameintervals': values})+'</script>'


class MetalReportTests(unittest.TestCase):
    def test_actual_intervals_override_misleading_summary(self):
        html = '<div>FPS 60; Missed Frames 0</div>'+fixture([16_666_667, 33_333_333])
        result = report.summarize(html)
        self.assertEqual(result['intervalsOver25MS'], 1)
        self.assertEqual(result['extraRefreshIntervalsAt60Hz'], 1)
        self.assertAlmostEqual(result['meanFPS'], 40)

    def test_short_clean_report_is_not_complete_acceptance(self):
        result = report.summarize(fixture([16_666_667]*299))
        self.assertEqual(result['intervalsOver25MS'], 0)
        self.assertFalse(result['overallComplete'])

    def test_missing_raw_evidence_fails(self):
        with self.assertRaises(ValueError):
            report.summarize('<div>60 FPS</div>')

    def test_duplicate_payload_fails(self):
        with self.assertRaises(ValueError):
            report.summarize(fixture([16_666_667])*2)

    def test_invalid_values_fail(self):
        for values in ([], [0], [-1], [float('nan')], [float('inf')], ['16666667'], [True]):
            with self.subTest(values=values), self.assertRaises(ValueError):
                report.summarize(fixture(values))


if __name__ == '__main__':
    unittest.main()
