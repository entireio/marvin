#!/usr/bin/env python3
"""Regression checks for interpretation of Apple's per-frame interval export."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('overview',Path(__file__).with_name('read-game-overview.py'))
overview=importlib.util.module_from_spec(spec)
spec.loader.exec_module(overview)


class OverviewTests(unittest.TestCase):
    def rows(self,starts=(0,16_666_667,50_000_000)):
        return [{'frame':i,'start':s,'duration':16_666_625,'end':s+16_666_625} for i,s in enumerate(starts)]

    def test_held_frame_uses_adjacent_starts(self):
        result=overview.summarize({('1','game','5','2.On Display'):self.rows()})
        metric=result['metrics'][0]
        self.assertEqual(metric['distribution']['over25MS'],1)
        self.assertAlmostEqual(metric['distribution']['maxMS'],33.333333)
        self.assertFalse(result['overallComplete'])

    def test_layer_and_process_are_not_merged(self):
        groups={(pid,'game',layer,'2.On Display'):self.rows() for pid,layer in [('1','5'),('1','6'),('2','5')]}
        self.assertEqual(len(overview.summarize(groups)['metrics']),3)

    def test_missing_and_duplicate_frames_are_visible(self):
        for numbers in ([0,2,3],[0,1,1]):
            rows=self.rows()
            for row,number in zip(rows,numbers):row['frame']=number
            self.assertFalse(overview.summarize({('1','game','5','2.On Display'):rows})['metrics'][0]['frameIDsContiguous'])

    def test_gpu_envelope_uses_duration_not_submission_spacing(self):
        result=overview.summarize({('1','game','5','1.GPU Begin to End'):self.rows()})['metrics'][0]
        self.assertEqual(result['distribution']['over25MS'],0)
        self.assertEqual(result['distribution']['count'],3)

    def test_quantization_is_reported_not_silently_erased(self):
        result=overview.summarize({('1','game','5','2.On Display'):self.rows((0,16_666_667,33_333_334))})['metrics'][0]
        self.assertEqual(result['maximumAbsoluteEndToNextStartNS'],42)

    def test_terminal_held_frame_is_reported_separately(self):
        for rows in (self.rows(),self.rows()[:1]):
            rows[-1]['duration']=50_000_000
            rows[-1]['end']=rows[-1]['start']+50_000_000
            result=overview.summarize({('1','game','5','2.On Display'):rows})['metrics'][0]
            self.assertEqual(result['terminalDisplayDurationMS'],50)
            self.assertTrue(result['terminalDisplayOver25MS'])
            self.assertEqual(result['cadenceCoverageEndSeconds'],rows[-1]['start']/1e9)
            if len(rows)==1:self.assertIsNone(result['distribution'])

    def test_xml_references_and_raw_units(self):
        columns=('start','duration','end','name','process','process-name','layer-id','label')
        schema='<schema name="metal-perf-overview-layer-per-frame-interval-metric">'+''.join(f'<col><mnemonic>{c}</mnemonic></col>' for c in columns)+'</schema>'
        row='<row><time id="s">1000000000</time><duration>16666625</duration><time>1016666625</time><name>2.On Display</name><process><pid>4</pid></process><name>game</name><uint64>8</uint64><string>16.67 ms (Frame 0)</string></row>'
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'export.xml'
            path.write_text('<trace-query-result><node>'+schema+row+'</node></trace-query-result>')
            groups=overview.read_intervals(path)
            self.assertEqual(groups[('4','game','8','2.On Display')][0]['start'],1_000_000_000)
            valid=path.read_text()
            path.write_text(valid.replace('</trace-query-result>','<node>'+schema+row+'</node></trace-query-result>'))
            with self.assertRaises(ValueError):overview.read_intervals(path)
            path.write_text(valid)
            path.write_text(path.read_text().replace('<time id="s">1000000000</time>','<time ref="s"/>') .replace('</node>','<time id="s">1000000000</time></node>'))
            self.assertEqual(overview.read_intervals(path),groups)
            path.write_text(path.read_text().replace('1016666625','1016666626'))
            with self.assertRaises(ValueError):overview.read_intervals(path)


if __name__=='__main__':unittest.main()
