#!/usr/bin/env python3
"""Exercise acceptance failures that previously escaped short average-only checks."""
import copy
import importlib.util
import unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('gate',Path(__file__).with_name('check-sustained-performance.py'))
gate=importlib.util.module_from_spec(spec);spec.loader.exec_module(gate)

class SustainedGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report=dict(startUptime=1000,durationSeconds=603,drawableWidth=1920,drawableHeight=1080,metalRenderer=True,townEnabled=True,audioActive=True,benchmarkArguments=['--city-roam'],gpuDevice='Test GPU',benchmarkRunID='test-run',quality=dict(msaaSamples=2,shadowMapWidths=[2048,4096],explorationDetail=True))
        cls.frames=[[1003+i/60,1000/60] for i in range(36001)]
        cls.updates=[[1003+i/60,3+i/60,0,0,0,0,0,0,0,i/30,0,0] for i in range(36001)]
        cls.hud=[dict(elapsedSeconds=3.5+i*.5,fps=60,text='60.0 FPS') for i in range(1200)]
        cls.gpu=dict(metricKind='encoderBusyUnion',runID='test-run',measuredSeconds=600,runStartUptime=1000,gpuDevice='Test GPU',resolution=[1920,1080],minutes=[dict(start=3+i*60,end=3+(i+1)*60,firstElapsed=4+i*60,lastElapsed=62+i*60,maximumBatchGapSeconds=.5,samples=1000,p99MS=10,maxMS=12) for i in range(10)])
    def result(self,frames=None,report=None,hud=None,gpu=None):
        return gate.evaluate(report or self.report,dict(renderFrames=self.frames if frames is None else frames,updates=self.updates),self.hud if hud is None else hud,self.gpu if gpu is None else gpu)
    def test_valid_complete_ledger(self):
        self.assertTrue(self.result()['passed'])
    def test_legacy_gpu_envelope_cannot_claim_headroom(self):
        self.assertFalse(self.result(gpu=dict(self.gpu,metricKind='commandBufferEnvelope'))['passed'])
    def test_callback_and_gpu_do_not_prove_presentation(self):
        self.assertFalse(self.result()['overallComplete'])
        self.assertFalse(self.result()['presentationVerified'])
    def test_missing_frame_cannot_hide_gap(self):
        frames=self.frames.copy();del frames[21000]
        self.assertFalse(self.result(frames=frames)['passed'])
    def test_evenly_slow_below_25ms_fails_coverage(self):
        frames=[[1003+i*.022,22] for i in range(27273)]
        r=self.result(frames=frames);self.assertEqual(r['gapsOver25MS'],0);self.assertFalse(r['passed'])
    def test_short_run_fails(self):
        self.assertFalse(self.result(frames=self.frames[:7200])['passed'])
    def test_spike_fails_even_with_good_average(self):
        frames=copy.deepcopy(self.frames);frames[21000][1]=33.3
        self.assertFalse(self.result(frames=frames)['passed'])
    def test_timestamp_consistent_missed_frame(self):
        frames=copy.deepcopy(self.frames)
        frames[21000][0]+=.010;frames[21000][1]+=10;frames[21001][1]-=10
        result=self.result(frames=frames)
        self.assertEqual(result['gapsOver25MS'],1);self.assertFalse(result['passed'])
        self.assertFalse(any('disagree with timestamps' in f for f in result['failures']))
    def test_teleport_is_not_driving(self):
        result=gate.evaluate(self.report,dict(renderFrames=self.frames,updates=[self.updates[0],self.updates[-1]]),self.hud,self.gpu)
        self.assertFalse(result['passed'])
    def test_sustained_58_counter_fails(self):
        self.assertFalse(self.result(hud=[dict(h,fps=58) for h in self.hud])['passed'])
    def test_false_counter_fails(self):
        hud=[dict(h,fps=1) for h in self.hud]
        self.assertFalse(self.result(hud=hud)['passed'])
    def test_wrong_gpu_run_fails(self):
        self.assertFalse(self.result(gpu=dict(self.gpu,runStartUptime=999))['passed'])
    def test_invalid_gpu_times_fail(self):
        gpu=copy.deepcopy(self.gpu);gpu['minutes'][-1]['p99MS']=float('-inf')
        self.assertFalse(self.result(gpu=gpu)['passed'])
    def test_instrumentation_stall_fails_even_when_rendering_continues(self):
        updates=self.updates.copy();del updates[21000:21012]
        result=gate.evaluate(self.report,dict(renderFrames=self.frames,updates=updates),self.hud,self.gpu)
        self.assertFalse(result['passed'])
        self.assertTrue(any('Simulation-update gap' in f for f in result['failures']))
    def test_repeated_gpu_minute_is_not_full_coverage(self):
        gpu=copy.deepcopy(self.gpu)
        for minute in gpu['minutes']:
            minute['firstElapsed']=4;minute['lastElapsed']=62
        self.assertFalse(self.result(gpu=gpu)['passed'])
    def test_nonfinite_position_fails(self):
        updates=copy.deepcopy(self.updates);updates[200][9]=float('inf')
        self.assertFalse(gate.evaluate(self.report,dict(renderFrames=self.frames,updates=updates),self.hud,self.gpu)['passed'])
    def test_unknown_diagnostic_flag_fails(self):
        report=dict(self.report,benchmarkArguments=['--city-roam','--benchmark-mystery-quality-change'])
        self.assertFalse(self.result(report=report)['passed'])
    def test_native_gpu_capture_cannot_pass_as_production(self):
        report=dict(self.report,benchmarkArguments=['--city-roam','--benchmark-gpu-capture'])
        self.assertFalse(self.result(report=report)['passed'])
    def test_experimental_mesh_cannot_pass_as_production(self):
        for flag in ['--benchmark-tangent-reuse','--benchmark-exact-tangents','--benchmark-shadow-batch','--benchmark-shadow-batch-live']:
            report=dict(self.report,benchmarkArguments=['--city-roam',flag])
            self.assertFalse(self.result(report=report)['passed'])
    def test_empty_counter_fails(self):
        self.assertFalse(self.result(hud=[])['passed'])
    def test_diagnostic_quality_flag_fails(self):
        report=dict(self.report,benchmarkArguments=['--city-roam','--benchmark-isolate-trails'])
        self.assertFalse(self.result(report=report)['passed'])
    def test_gpu_headroom_is_required(self):
        r=gate.evaluate(self.report,dict(renderFrames=self.frames,updates=self.updates),self.hud)
        self.assertTrue(r['callbackGatePassed']);self.assertFalse(r['passed'])
    def test_late_gpu_budget_failure(self):
        gpu=copy.deepcopy(self.gpu);gpu['minutes'][-1]['p99MS']=15
        self.assertFalse(self.result(gpu=gpu)['passed'])
    def test_temporary_stop_fails_despite_sufficient_total_distance(self):
        updates=copy.deepcopy(self.updates)
        for i,u in enumerate(updates):
            u[9]=min(i/30,240) if i<10800 else i/30-120
        result=gate.evaluate(self.report,dict(renderFrames=self.frames,updates=updates),self.hud,self.gpu)
        self.assertGreater(result['distanceMeters'],1000)
        self.assertTrue(any('Insufficient driving during seconds 120' in f for f in result['failures']))
        self.assertFalse(result['passed'])
    def test_update_slowdown_cannot_hide_behind_smooth_rendering(self):
        updates=[u for i,u in enumerate(self.updates) if not (12000<=i<12600 and i%20==0)]
        result=gate.evaluate(self.report,dict(renderFrames=self.frames,updates=updates),self.hud,self.gpu)
        self.assertEqual(result['gapsOver25MS'],0)
        self.assertTrue(any('Rolling simulation-update rate' in f for f in result['failures']))
        self.assertFalse(result['passed'])
    def test_reduced_detail_manifest_fails(self):
        for key,value in [('msaaSamples',1),('shadowMapWidths',[1024,2048]),('explorationDetail',False)]:
            with self.subTest(key=key):
                report=copy.deepcopy(self.report);report['quality'][key]=value
                self.assertFalse(self.result(report=report)['passed'])
    def test_no_frames_fails(self):
        self.assertFalse(self.result(frames=[])['passed'])

if __name__=='__main__':unittest.main()
