#!/usr/bin/env python3
"""Regression checks for interpretation of Apple's per-frame interval export."""
import importlib.util
import copy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('overview',Path(__file__).with_name('read-game-overview.py'))
overview=importlib.util.module_from_spec(spec)
spec.loader.exec_module(overview)


def presentation_fixture(benchmark=None):
    """Synthetic 60 Hz ledger with lifecycle rows and a post-window successor."""
    benchmark = benchmark or dict(startUptime=1000, durationSeconds=606, benchmarkRunID='fixture-run',sandstorm=False,daylightFraction=.5)
    offset = overview.seconds_ns(benchmark['startUptime'])
    duration = overview.seconds_ns(benchmark['durationSeconds'])
    metrics = {name: [] for name in ('On Display', 'CPU Begin to Present', 'GPU Begin to End')}
    for i in range(6, 36_211):
        start, next_start = round(i*overview.NS/60), round((i+1)*overview.NS/60)
        for name, a, b in [('On Display', start, next_start-42),
                           ('CPU Begin to Present', start-10_000_000, start-2_000_000),
                           ('GPU Begin to End', start-6_000_000, start-1_000_000)]:
            metrics[name].append(dict(frame=i, start=a, end=b, duration=b-a))
    return benchmark, dict(schemaVersion=1, metricKind=overview.PRESENTATION_KIND,
        benchmarkSHA256=overview.json_hash(benchmark), runID=benchmark['benchmarkRunID'],
        binarySHA256='a'*64, pid=123, processName='MarvinSimulator', layerID='456', layerName=overview.LAYER_NAME,
        markers={name: dict(traceNS=ns, markerUptimeNS=offset+ns, boundaryUptimeNS=offset+ns)
                 for name, ns in [('TownBenchmarkStart', 0), ('TownBenchmarkEnd', duration)]},
        trace=dict(runNumber=1, pid=123, template='Game Performance Overview',
                   durationNS=duration+overview.NS, endReason='Target app exited', returnExitStatus='0', environment={}),
        intervals=metrics)


class PresentationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.benchmark, cls.ledger = presentation_fixture()

    def result(self, ledger=None, benchmark=None):
        return overview.verify_presentation_ledger(self.ledger if ledger is None else ledger,
                                            self.benchmark if benchmark is None else benchmark)

    def test_complete_evidence_is_not_overall_acceptance(self):
        result = self.result()
        self.assertTrue(result['presentationVerified'], result)
        self.assertTrue(result['presentationGatePassed'], result)
        self.assertEqual(result['measuredFrames'], 36000)
        self.assertFalse(result['overallComplete'])

    def test_saved_flags_never_bypass_validation(self):
        ledger = dict(self.ledger, intervals={}, validation={'presentationVerified':True, 'presentationGatePassed':True})
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_duplicate_and_conflicting_events_fail(self):
        for conflict in (False, True):
            ledger = copy.deepcopy(self.ledger)
            row = dict(ledger['intervals']['On Display'][1000])
            if conflict: row['start'] += 1
            ledger['intervals']['On Display'].append(row)
            self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_missing_interior_frame_fails_even_with_repaired_hold(self):
        ledger = copy.deepcopy(self.ledger)
        rows = ledger['intervals']['On Display']
        removed = rows.pop(1000)
        rows[999]['end'] = removed['end']
        rows[999]['duration'] = rows[999]['end']-rows[999]['start']
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_real_long_hold_is_valid_evidence_but_fails_cadence(self):
        ledger = copy.deepcopy(self.ledger)
        rows = ledger['intervals']['On Display']
        rows[1000]['start'] += 10_000_000
        rows[1000]['duration'] -= 10_000_000
        rows[999]['end'] += 10_000_000
        rows[999]['duration'] += 10_000_000
        result = self.result(ledger)
        self.assertTrue(result['presentationVerified'], result)
        self.assertFalse(result['presentationGatePassed'])
        self.assertEqual(result['gapsOver25MS'], 1)

    def test_pending_post_window_event_is_not_counted(self):
        ledger = copy.deepcopy(self.ledger)
        ledger['intervals']['CPU Begin to Present'].append(dict(frame=99999, start=604*overview.NS, end=None, duration=None))
        result = self.result(ledger)
        self.assertTrue(result['presentationGatePassed'], result)
        self.assertEqual(result['measuredFrames'], 36000)
        self.assertEqual(len(result['boundaryOrPendingEvents']), 1)

    def test_interior_or_unknown_pending_event_fails(self):
        for start in (None, 10*overview.NS):
            ledger = copy.deepcopy(self.ledger)
            ledger['intervals']['CPU Begin to Present'].append(dict(frame=99999, start=start, end=None, duration=None))
            self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_unpresented_interior_work_fails(self):
        ledger = copy.deepcopy(self.ledger)
        ledger['intervals']['GPU Begin to End'].append(dict(frame=99999,start=10*overview.NS,end=10*overview.NS+1,duration=1))
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_unpresented_work_crossing_window_boundary_fails(self):
        ledger = copy.deepcopy(self.ledger)
        ledger['intervals']['GPU Begin to End'].append(dict(frame=0,start=2*overview.NS,end=4*overview.NS,duration=2*overview.NS))
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_terminal_duration_cannot_replace_successor_event(self):
        ledger = copy.deepcopy(self.ledger)
        rows = [r for r in ledger['intervals']['On Display'] if r['start']<603*overview.NS]
        rows[-1]['end'] = 604*overview.NS
        rows[-1]['duration'] = rows[-1]['end']-rows[-1]['start']
        ledger['intervals']['On Display'] = rows
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_missing_lifecycle_or_late_gpu_completion_fails(self):
        for missing in (False, True):
            ledger = copy.deepcopy(self.ledger)
            rows = ledger['intervals']['GPU Begin to End']
            if missing: rows.pop(1000)
            else:
                rows[1000]['end'] += 2_000_000
                rows[1000]['duration'] += 2_000_000
            self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_stale_gpu_times_cannot_reuse_plausible_frame_ids(self):
        ledger = copy.deepcopy(self.ledger)
        for row in ledger['intervals']['GPU Begin to End']:
            row.update(start=1,end=2,duration=1)
        result=self.result(ledger)
        self.assertFalse(result['presentationVerified'])
        self.assertIn('precedes its frame CPU',result['failures'][0])

    def test_cpu_gpu_overlap_is_valid(self):
        # The fixture overlaps both intervals: no serialization is presumed.
        gpu=self.ledger['intervals']['GPU Begin to End'][1000]
        cpu=self.ledger['intervals']['CPU Begin to Present'][1000]
        self.assertLess(gpu['start'],cpu['end'])
        self.assertTrue(self.result()['presentationVerified'])

    def test_wrong_run_layer_pid_binary_or_benchmark_fails(self):
        for key, value in [('runID','other'),('layerName','other'),('pid',124),('binarySHA256','')]:
            with self.subTest(key=key):
                self.assertFalse(self.result(dict(self.ledger, **{key:value}))['presentationVerified'])
        self.assertFalse(self.result(benchmark=dict(self.benchmark,startUptime=999))['presentationVerified'])

    def test_clock_drift_duration_and_trace_coverage_fail(self):
        for mutation in ('drift','duration','coverage'):
            ledger = copy.deepcopy(self.ledger)
            if mutation == 'drift': ledger['markers']['TownBenchmarkEnd']['markerUptimeNS'] += 200_000
            elif mutation == 'duration': ledger['markers']['TownBenchmarkEnd']['boundaryUptimeNS'] -= 2_000_000
            else: ledger['trace']['durationNS'] = 602*overview.NS
            self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_end_quantization_is_bounded(self):
        ledger = copy.deepcopy(self.ledger)
        row = ledger['intervals']['On Display'][1000]
        row['end'] -= 1000
        row['duration'] -= 1000
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_capture_hud_and_abnormal_end_are_not_acceptance(self):
        for key in overview.DISABLED_INSTRUMENTATION:
            ledger = copy.deepcopy(self.ledger)
            ledger['trace']['environment'][key] = '1'
            self.assertFalse(self.result(ledger)['presentationVerified'])
        ledger = copy.deepcopy(self.ledger)
        ledger['trace']['endReason'] = 'Time limit reached'
        self.assertFalse(self.result(ledger)['presentationVerified'])

    def test_unexplained_diagnostic_environment_rejected_even_when_zero(self):
        for key in ('MARVIN_GPU_PRODUCTION_SHADOW_BATCH','MARVIN_FUTURE_SWITCH',
                    '__XPC_MARVIN_DEBUG','MTL_HUD_UNKNOWN_TIMING','MTL_CAPTURE_SCOPE',
                    '__XPC_MTL_HUD_ENABLED','__XPC_MTL_CAPTURE_ENABLED'):
            ledger=copy.deepcopy(self.ledger)
            ledger['trace']['environment'][key]='0'
            self.assertFalse(self.result(ledger)['presentationVerified'],key)

    def test_trace_production_controls_must_match_benchmark(self):
        ledger=dict(self.ledger,trace=dict(self.ledger['trace'],environment={
            **{key:'0' for key in overview.DISABLED_INSTRUMENTATION},
            'MARVIN_BENCHMARK_SECONDS':'606','MARVIN_SANDSTORM':'0','MARVIN_DAYLIGHT_FRACTION':'0.5'}))
        self.assertTrue(self.result(ledger)['presentationVerified'])
        for key,value in [('MARVIN_SANDSTORM','1'),('MARVIN_DAYLIGHT_FRACTION','0.2'),('MARVIN_BENCHMARK_SECONDS','607')]:
            broken=copy.deepcopy(ledger);broken['trace']['environment'][key]=value
            self.assertFalse(self.result(broken)['presentationVerified'])


class PresentationXMLTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.benchmark = dict(startUptime=1000,durationSeconds=606,benchmarkRunID='fixture-run',sandstorm=False,daylightFraction=.5)
        self.manifest = dict(binarySHA256='a'*64,benchmarkRunID='fixture-run',actualDurationSeconds=606,
            requestedDurationSeconds=606,measuredWindowSeconds=[3,603],measurementWindowEndExclusive=True,
            presentation=True,storm=False,daylight=.5,gpuHUD=False)
        self.intervals, self.signposts, self.toc = [self.root/name for name in ('intervals.xml','signposts.xml','toc.xml')]
        columns = ('start','duration','end','name','process','process-name','layer-id','layer-name','label')
        schema = self.schema('metal-perf-overview-layer-per-frame-interval-metric',columns)
        rows = []
        # Intentionally sparse, but every held presentation is fully accounted:
        # valid import must succeed while its cadence gate fails.
        starts = [100_000_000,3*overview.NS,603*overview.NS,604*overview.NS,605*overview.NS]
        for i, (start, next_start) in enumerate(zip(starts,starts[1:])):
            for n, a, b in [('2.On Display',start,next_start-42),
                            ('0.CPU Begin to Present',start-10_000_000,start-2_000_000),
                            ('1.GPU Begin to End',start-6_000_000,start-1_000_000)]:
                process = '<process id="p"><pid>123</pid></process>' if not rows else '<process ref="p"/>'
                rows.append(f'<row><time fmt="wrong">{a}</time><duration>{b-a}</duration><time>{b}</time><string>{n}</string>{process}<string>MarvinSimulator</string><uint64>456</uint64><string>{overview.LAYER_NAME}</string><string>ignored fmt (Frame {i})</string></row>')
        self.intervals.write_text(self.export(schema+''.join(rows)))
        schema = self.schema('os-signpost',('time','process','name','subsystem','message'))
        rows=[]
        for name, seconds in [('TownBenchmarkStart',0),('TownBenchmarkEnd',606)]:
            process = '<process id="p"><pid>123</pid></process>' if not rows else '<process ref="p"/>'
            message = f'<os-log-metadata fmt="localized/truncated"><narrative-text>run=</narrative-text><string>fixture-run</string><narrative-text> markerUptime=</narrative-text><fixed-decimal fmt="wrong">{1000+seconds}.000000007</fixed-decimal><narrative-text> benchmarkBoundaryUptime=</narrative-text><fixed-decimal>{1000+seconds}.000000000</fixed-decimal></os-log-metadata>'
            rows.append(f'<row><time>{seconds*overview.NS+7}</time>{process}<string>{name}</string><string>io.entire.marvin.performance</string>{message}</row>')
        self.signposts.write_text(self.export(schema+''.join(rows)))
        self.toc.write_text('<trace-toc><run number="1"><info><target><process name="MarvinSimulator" pid="123" return-exit-status="0"/><environment/></target><summary><duration>607</duration><end-reason>Target app exited</end-reason><template-name>Game Performance Overview</template-name></summary></info></run></trace-toc>')

    @staticmethod
    def schema(name, columns):
        return f'<schema name="{name}">'+''.join(f'<col><mnemonic>{c}</mnemonic></col>' for c in columns)+'</schema>'

    @staticmethod
    def export(body):
        return '<trace-query-result><node xpath="//trace-toc[1]/run[1]/data[1]/table[1]">'+body+'</node></trace-query-result>'

    def read(self):
        return overview.import_presentation(self.intervals,self.signposts,self.toc,self.benchmark,self.manifest)

    def test_import_binds_raw_units_refs_markers_and_records_slow_evidence(self):
        result=self.read()
        self.assertTrue(result['validation']['presentationVerified'],result['validation'])
        self.assertFalse(result['validation']['presentationGatePassed'])
        self.assertEqual(result['validation']['clockOffsetSpreadNS'],0)
        self.assertEqual(result['markers']['TownBenchmarkStart']['markerUptimeNS'],1000*overview.NS+7)
        self.assertEqual(len(result['sourceSHA256']),3)
        validated=overview.verify_presentation(result,self.benchmark,self.manifest)
        self.assertTrue(validated['provenanceVerified'],validated)
        self.assertTrue(validated['presentationVerified'],validated)
        self.assertFalse(validated['presentationGatePassed'])

    def test_manifest_binary_hash_and_source_provenance_are_required(self):
        original=self.read()
        for key,value in [('binarySHA256','b'*64),('manifestSHA256','b'*64),('sourceSHA256',{}),('sourcePaths',{})]:
            result=overview.verify_presentation(dict(original,**{key:value}),self.benchmark,self.manifest)
            self.assertFalse(result['presentationVerified'],key)
        self.assertFalse(overview.verify_presentation(original,self.benchmark)['presentationVerified'])

    def test_modified_raw_exports_and_modified_saved_ledger_fail(self):
        original=self.read()
        changed=copy.deepcopy(original)
        changed['intervals']['GPU Begin to End'][0]['start']+=1
        changed['intervals']['GPU Begin to End'][0]['duration']-=1
        self.assertFalse(overview.verify_presentation(changed,self.benchmark,self.manifest)['presentationVerified'])
        self.intervals.write_text(self.intervals.read_text()+'\n')
        self.assertFalse(overview.verify_presentation(original,self.benchmark,self.manifest)['presentationVerified'])

    def test_finalized_manifest_identity_window_weather_and_actual_duration(self):
        original=dict(self.manifest)
        for key,value in [('benchmarkRunID',None),('actualDurationSeconds',605),('measuredWindowSeconds',[0,600]),
                          ('measurementWindowEndExclusive',False),('presentation',False),('storm',True),('daylight',.2),('gpuHUD',True)]:
            self.manifest=dict(original,**{key:value})
            with self.subTest(key=key),self.assertRaises(ValueError):self.read()
        self.manifest=dict(original,requestedDurationSeconds=605.9)
        self.assertTrue(self.read()['validation']['presentationVerified'])

    def test_cli_writes_ledger_and_returns_validity_not_cadence(self):
        benchmark,manifest,output=[self.root/name for name in ('benchmark.json','manifest.json','presentation.json')]
        benchmark.write_text(json.dumps(self.benchmark));manifest.write_text(json.dumps(self.manifest))
        argv=['read-game-overview.py',str(self.intervals),'--benchmark',str(benchmark),'--manifest',str(manifest),'--toc',str(self.toc),'--signposts',str(self.signposts),'--output',str(output)]
        with patch('sys.argv',argv),contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(overview.main(),0)
        self.assertFalse(json.loads(output.read_text())['validation']['presentationGatePassed'])
        self.intervals.write_text('invalid XML')
        with patch('sys.argv',argv),contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(overview.main(),1)
        self.assertFalse(json.loads(output.read_text())['validation']['presentationVerified'])

    def test_wrong_or_duplicate_markers_fail(self):
        original=self.signposts.read_text()
        self.signposts.write_text(original.replace('fixture-run','another-run'))
        with self.assertRaises(ValueError):self.read()
        # Duplicate a fully referential event; no duplicate XML ID involved.
        row=original[original.rfind('<row>'):original.rfind('</row>')+6]
        self.signposts.write_text(original.replace('</node>',row+'</node>'))
        with self.assertRaises(ValueError):self.read()

    def test_multiple_export_nodes_or_runs_and_unresolved_refs_fail(self):
        original=self.intervals.read_text()
        for text in (original.replace('</trace-query-result>','<node/></trace-query-result>'),
                     original.replace('/run[1]','/run[2]'),
                     original.replace('ref="p"','ref="missing"')):
            self.intervals.write_text(text)
            with self.assertRaises(ValueError):self.read()

    def test_sentinel_is_unknown_never_zero(self):
        text=self.intervals.read_text().replace('<duration>2899999958</duration>','<sentinel/>',1)
        self.intervals.write_text(text)
        groups,_=overview.read_presentation_rows(self.intervals)
        self.assertIsNone(next(iter(groups.values()))['On Display'][0]['duration'])
        self.assertFalse(self.read()['validation']['presentationVerified'])

    def test_foreign_layer_and_manifest_run_fail(self):
        self.intervals.write_text(self.intervals.read_text().replace(overview.LAYER_NAME,'Another layer'))
        with self.assertRaises(ValueError):self.read()
        self.manifest['benchmarkRunID']='other'
        with self.assertRaises(ValueError):self.read()


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
