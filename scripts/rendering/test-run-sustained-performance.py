#!/usr/bin/env python3
"""Runner contract/failure tests. Every subprocess is fake; no native workload."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('sustained_runner', Path(__file__).with_name('run-sustained-performance.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
TOC = '''<trace-toc><run number="1"><data>
<table schema="metal-perf-overview-layer-per-frame-interval-metric"/>
<table schema="os-signpost" category="InduceCondition" subsystem="condition"/>
<table schema="os-signpost" category="PointsOfInterest" target-pid="SINGLE"/>
<table schema="os-signpost" category="PointsOfInterest" target-pid="SINGLE" dynamic-tracing-enabled-subsystems="privacy"/>
</data></run></trace-toc>'''


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root/'evidence'
        self.output.mkdir()
        self.plan = runner.make_plan(self.root, self.output, presentation=True,
                                     inherited_environment={'PATH':'/usr/bin'})
        self.plan['binary'].parent.mkdir(parents=True)
        self.plan['binary'].write_bytes(b'fixture binary; never executed')
        self.manifest = {'binarySHA256':hashlib.sha256(self.plan['binary'].read_bytes()).hexdigest(),
                         'requestedDurationSeconds':606, 'storm':False, 'daylight':.5,
                         'measuredWindowSeconds':[3,603], 'presentation':True}
        self.calls = []

    def fake_run(self, command, **kwargs):
        self.calls.append(command)
        if command == self.plan['launch']:
            self.plan['trace'].mkdir()
            (self.plan['trace']/'metadata').write_text('mock trace')
            (self.output/'benchmark.json').write_text(json.dumps(
                {'durationSeconds':606.012, 'benchmarkRunID':'fixture-run',
                 'sandstorm':False, 'daylightFraction':.5}))
        elif command[:3] == ['xcrun','xctrace','export']:
            Path(command[command.index('--output')+1]).write_text(TOC if '--toc' in command else '<mock-export/>')
        elif command == self.plan['import']:
            (self.output/'presentation.json').write_text(json.dumps(
                {'validation':{'presentationVerified':True,'presentationGatePassed':False}}))
        elif command == self.plan['gate']:
            return subprocess.CompletedProcess(command, 1)  # Valid evidence, failed cadence.
        return subprocess.CompletedProcess(command, 0)

    def execute(self, fake=None):
        with contextlib.redirect_stderr(io.StringIO()):
            return runner.execute_plan(self.plan, self.manifest, run=fake or self.fake_run)

    def test_environment_removes_all_inherited_diagnostic_overrides(self):
        inherited = {'PATH':'/usr/bin', 'HOME':'/fixture/home', 'MARVIN_AUDIO_CACHE':'1',
                     'MARVIN_BENCHMARK_SECONDS':'10', 'MARVIN_GROUND_PACK_DIRECTORY':'/tmp/candidate',
                     'MARVIN_FUTURE_UNKNOWN_SWITCH':'1', '__XPC_MARVIN_DEBUG':'1',
                     'MTL_CAPTURE_ENABLED':'1', 'MTL_CAPTURE_SCOPE':'all',
                     'MTL_HUD_ENABLED':'1', 'MTL_HUD_UNKNOWN_TIMING':'1', '__XPC_MTL_HUD_ENABLED':'1'}
        plan = runner.make_plan(self.root, self.output, presentation=True, storm=True, daylight=.2,
                                inherited_environment=inherited)
        env = plan['environment']
        self.assertEqual({k:v for k,v in env.items() if k.startswith('MARVIN_')},
                         {'MARVIN_BENCHMARK_SECONDS':'606','MARVIN_DAYLIGHT_FRACTION':'0.2','MARVIN_SANDSTORM':'1'})
        self.assertEqual(env['HOME'], '/fixture/home')
        for key in runner.DISABLED_INSTRUMENTATION:
            self.assertEqual(env[key], '0')
        for key in ('MARVIN_AUDIO_CACHE','MARVIN_GROUND_PACK_DIRECTORY','MARVIN_FUTURE_UNKNOWN_SWITCH',
                    '__XPC_MARVIN_DEBUG','MTL_CAPTURE_SCOPE','MTL_HUD_UNKNOWN_TIMING','__XPC_MTL_HUD_ENABLED'):
            self.assertNotIn(key, env)
        self.assertEqual(inherited['MTL_CAPTURE_ENABLED'], '1')  # Caller env untouched.

    def test_sunrise_and_sunset_record_applied_daylight_separately(self):
        for requested, applied in [(0, .015), (1, .985)]:
            with self.subTest(requested=requested):
                plan = runner.make_plan(self.root, self.output, presentation=True,
                                        daylight=requested, inherited_environment={})
                self.assertEqual(plan['requestedDaylight'], requested)
                self.assertEqual(plan['daylight'], applied)
                self.assertEqual(plan['controlledEnvironment']['MARVIN_DAYLIGHT_FRACTION'], str(applied))

    def test_record_command_has_points_of_interest_tail_and_only_production_flags(self):
        command = self.plan['launch']
        self.assertEqual(command[command.index('--template')+1], 'Game Performance Overview')
        self.assertEqual(command[command.index('--instrument')+1], 'Points of Interest')
        self.assertEqual(command[command.index('--time-limit')+1], '660s')
        target_env = [command[i+1] for i,x in enumerate(command) if x == '--env']
        self.assertIn('MARVIN_BENCHMARK_SECONDS=606', target_env)
        self.assertIn('MTL_CAPTURE_ENABLED=0', target_env)
        self.assertIn('MTL_HUD_ENABLED=0', target_env)
        self.assertEqual(command[command.index('--launch')+1:],
                         ['--',str(self.plan['binary']),'--town-benchmark',str(self.output),'--city-roam'])
        self.assertNotIn('--attach', command)
        self.assertNotIn('--append-run', command)
        self.assertNotIn('--window', command)

    def test_exports_select_exactly_run_one_and_two_required_tables(self):
        exports = self.plan['exports']
        self.assertEqual(len(exports), 3)
        self.assertIn('--toc', exports[0][1])
        self.assertNotIn('--xpath', exports[0][1])
        self.assertEqual([cmd[cmd.index('--xpath')+1] for _,cmd,_ in exports[1:]],
                         ['/trace-toc/run[1]/data/table[@schema="metal-perf-overview-layer-per-frame-interval-metric"]',
                          runner.SIGNPOST_XPATH])

    def test_toc_selects_only_the_unrestricted_points_of_interest_table(self):
        path = self.output/'toc.xml'; path.write_text(TOC)
        runner.verify_export_tables(path)
        self.assertIn('not(@dynamic-tracing-enabled-subsystems)', runner.SIGNPOST_XPATH)
        self.assertIn('not(@subsystem)', runner.SIGNPOST_XPATH)

    def test_ambiguous_points_of_interest_stops_before_metric_export(self):
        duplicate = '<table schema="os-signpost" category="PointsOfInterest" target-pid="SINGLE"/>'
        def ambiguous(command, **kwargs):
            result = self.fake_run(command, **kwargs)
            if '--toc' in command:
                (self.output/'toc.xml').write_text(TOC.replace('</data>', duplicate+'</data>'))
            return result
        self.assertEqual(self.execute(ambiguous), 1)
        self.assertEqual(self.calls[-1], self.plan['exports'][0][1])
        self.assertNotIn(self.plan['import'], self.calls)

    def test_missing_interval_table_fails_toc_validation(self):
        path=self.output/'toc.xml'
        path.write_text(TOC.replace('metal-perf-overview-layer-per-frame-interval-metric','other'))
        with self.assertRaises(runner.RunFailure):runner.verify_export_tables(path)

    def test_import_and_gate_contracts(self):
        command = self.plan['import']
        self.assertEqual(Path(command[1]).name, 'read-game-overview.py')
        self.assertEqual(Path(command[2]).name, 'intervals.xml')
        for flag, file in (('--benchmark','benchmark.json'),('--manifest','run-manifest.json'),
                           ('--toc','toc.xml'),('--signposts','signposts.xml'),('--output','presentation.json')):
            self.assertEqual(command[command.index(flag)+1], str(self.output/file))
        gate = self.plan['gate']
        self.assertEqual(gate[gate.index('--scope')+1], 'presentation')
        self.assertEqual(gate[gate.index('--presentation-report')+1], str(self.output/'presentation.json'))

    def test_default_stays_uninstrumented_603_second_cadence(self):
        plan = runner.make_plan(self.root, self.output, inherited_environment={'MARVIN_GPU_VARIANTS':'no-town'})
        self.assertEqual(plan['requestedDurationSeconds'],603)
        self.assertEqual(plan['launch'][0],str(plan['binary']))
        self.assertIsNone(plan['import']); self.assertFalse(plan['exports'])
        self.assertEqual(plan['gate'][-2:],['--scope','cadence'])
        self.assertNotIn('MARVIN_GPU_VARIANTS',plan['environment'])
        self.assertEqual(plan['environment']['MTL_HUD_ENABLED'],'0')

    def test_gpu_hud_remains_a_separate_profile_mode(self):
        plan = runner.make_plan(self.root,self.output,gpu_hud=True,inherited_environment={})
        self.assertEqual(plan['environment']['MTL_HUD_ENABLED'],'1')
        self.assertEqual(plan['environment']['MTL_CAPTURE_ENABLED'],'0')
        self.assertIsNotNone(plan['profile']);self.assertIsNone(plan['import'])
        self.assertIn('--gpu-report',plan['gate'])

    def test_hud_and_presentation_are_mutually_exclusive(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            runner.argument_parser().parse_args([str(self.output),'--gpu-hud','--presentation'])
        with self.assertRaises(ValueError):
            runner.make_plan(self.root,self.output,gpu_hud=True,presentation=True)

    def test_valid_but_slow_import_reaches_gate_and_returns_gate_failure(self):
        self.assertEqual(self.execute(),1)
        self.assertEqual(self.calls[-1],self.plan['gate'])
        saved=json.loads((self.output/'run-manifest.json').read_text())
        self.assertEqual(saved['requestedDurationSeconds'],606)
        self.assertEqual(saved['actualDurationSeconds'],606.012)
        self.assertEqual(saved['benchmarkRunID'],'fixture-run')
        status=json.loads((self.output/'runner-status.json').read_text())
        self.assertTrue(status['evidencePipelineCompleted'])
        self.assertFalse(status['overallComplete'])

    def test_record_failure_never_exports_or_gates(self):
        def fail(command,**kwargs):
            self.calls.append(command)
            return subprocess.CompletedProcess(command,2)
        self.assertEqual(self.execute(fail),2)
        self.assertEqual(self.calls,[self.plan['launch']])
        self.assertEqual(json.loads((self.output/'runner-status.json').read_text())['stage'],'record')

    def test_successful_record_without_trace_fails_closed(self):
        def empty(command,**kwargs):
            self.calls.append(command)
            return subprocess.CompletedProcess(command,0)
        self.assertEqual(self.execute(empty),1)
        self.assertEqual(self.calls,[self.plan['launch']])

    def test_drive_without_real_tail_does_not_export(self):
        def short(command,**kwargs):
            result=self.fake_run(command,**kwargs)
            if command==self.plan['launch']:
                (self.output/'benchmark.json').write_text(json.dumps({'durationSeconds':603.01,'benchmarkRunID':'fixture-run'}))
            return result
        self.assertEqual(self.execute(short),1)
        self.assertEqual(self.calls,[self.plan['launch']])

    def test_mislabeled_storm_stops_before_manifest_finalization_or_export(self):
        def wrong_weather(command, **kwargs):
            result=self.fake_run(command, **kwargs)
            if command==self.plan['launch']:
                path=self.output/'benchmark.json'; report=json.loads(path.read_text())
                report['sandstorm']=True;path.write_text(json.dumps(report))
            return result
        self.assertEqual(self.execute(wrong_weather),1)
        self.assertEqual(self.calls,[self.plan['launch']])
        self.assertNotIn('actualDurationSeconds',self.manifest)

    def test_mislabeled_daylight_stops_before_manifest_finalization_or_export(self):
        def wrong_daylight(command, **kwargs):
            result=self.fake_run(command, **kwargs)
            if command==self.plan['launch']:
                path=self.output/'benchmark.json'; report=json.loads(path.read_text())
                report['daylightFraction']=.8;path.write_text(json.dumps(report))
            return result
        self.assertEqual(self.execute(wrong_daylight),1)
        self.assertEqual(self.calls,[self.plan['launch']])
        self.assertNotIn('actualDurationSeconds',self.manifest)

    def test_binary_change_during_run_invalidates_evidence(self):
        def changed(command,**kwargs):
            result=self.fake_run(command,**kwargs)
            if command==self.plan['launch']:self.plan['binary'].write_bytes(b'different binary')
            return result
        self.assertEqual(self.execute(changed),1)
        self.assertNotIn(self.plan['import'],self.calls)

    def test_export_error_never_imports_or_gates(self):
        bad=self.plan['exports'][1][1]
        def fail(command,**kwargs):
            if command==bad:
                self.calls.append(command);return subprocess.CompletedProcess(command,3)
            return self.fake_run(command,**kwargs)
        self.assertEqual(self.execute(fail),3)
        self.assertEqual(self.calls[-1],bad)
        self.assertNotIn(self.plan['import'],self.calls)
        self.assertNotIn(self.plan['gate'],self.calls)

    def test_export_success_without_output_never_imports(self):
        bad=self.plan['exports'][2][1]
        def missing(command,**kwargs):
            if command==bad:
                self.calls.append(command);return subprocess.CompletedProcess(command,0)
            return self.fake_run(command,**kwargs)
        self.assertEqual(self.execute(missing),1)
        self.assertEqual(self.calls[-1],bad)
        self.assertNotIn(self.plan['import'],self.calls)

    def test_structurally_invalid_import_never_reaches_gate(self):
        def invalid(command,**kwargs):
            result=self.fake_run(command,**kwargs)
            if command==self.plan['import']:
                (self.output/'presentation.json').write_text('{"validation":{"presentationVerified":false}}')
                return subprocess.CompletedProcess(command,1)
            return result
        self.assertEqual(self.execute(invalid),1)
        self.assertEqual(self.calls[-1],self.plan['import'])
        self.assertNotIn(self.plan['gate'],self.calls)

    def test_import_success_without_report_never_reaches_gate(self):
        def missing(command,**kwargs):
            if command==self.plan['import']:
                self.calls.append(command);return subprocess.CompletedProcess(command,0)
            return self.fake_run(command,**kwargs)
        self.assertEqual(self.execute(missing),1)
        self.assertNotIn(self.plan['gate'],self.calls)


if __name__=='__main__':
    unittest.main()
