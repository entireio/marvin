#!/usr/bin/env python3
"""Associate legacy Metal HUD batches with benchmark time; never unique frames."""
import argparse
import datetime
import json
from pathlib import Path


def summarize(log, report):
    start=report['endWallTime']-report['durationSeconds']
    identities=[json.loads(line.split('MARVIN_BENCHMARK_ID ',1)[1]) for line in log.splitlines() if line.startswith('MARVIN_BENCHMARK_ID ')]
    identity=identities[0] if len(identities)==1 else {}
    batches=[];seen=set()
    for line in log.splitlines():
        if 'metal-HUD:' not in line:continue
        payload=line.split('metal-HUD:',1)[1].strip()
        if payload in seen:continue
        try:
            stamp=datetime.datetime.fromisoformat(line[:23]).timestamp()
            values=[float(v) for v in payload.split(',')]
        except ValueError:continue
        if len(values)<5 or (len(values)-3)%2:raise ValueError('Unexpected HUD format')
        seen.add(payload)
        batches.append(dict(elapsed=stamp-start,memoryMB=values[2],metalMB=values[1],gpu=values[4::2],intervals=values[3::2]))
    def window(a,b):
        # Batch timestamps are at the end; skip first second to exclude earlier samples.
        rows=[r for r in batches if a+1<=r['elapsed']<b]
        gpu=sorted(v for r in rows for v in r['gpu'])
        intervals=[v for r in rows for v in r['intervals']]
        if not gpu:return dict(start=a,end=b,samples=0)
        return dict(start=a,end=b,samples=len(gpu),firstElapsed=rows[0]['elapsed'],lastElapsed=rows[-1]['elapsed'],maximumBatchGapSeconds=max((b['elapsed']-a['elapsed'] for a,b in zip(rows,rows[1:])),default=999),p50MS=gpu[len(gpu)//2],p95MS=gpu[int((len(gpu)-1)*.95)],p99MS=gpu[int((len(gpu)-1)*.99)],maxMS=max(gpu),loggedIntervalsOver25MS=sum(v>25 for v in intervals),memoryPeakMB=max(r['memoryMB'] for r in rows),metalPeakMB=max(r['metalMB'] for r in rows))
    return dict(metricKind='commandBufferEnvelope',runID=identity.get('runID'),runStartUptime=identity.get('startUptime'),gpuDevice=identity.get('gpuDevice'),resolution=identity.get('resolution'),method='Deduplicated complete Metal HUD batches, indexed by log wall time; individual samples may repeat. HUD overhead present. Not unique-frame or encoder timing.',measuredSeconds=max(0,min(600,max((r['elapsed'] for r in batches),default=3)-3)),minutes=[window(3+i*60,3+(i+1)*60) for i in range(10)],isolationWindows=[window(a,b) for a,b in [(270,300),(300,330),(330,360),(450,480),(480,510),(510,540)]])

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);a=p.parse_args()
    r=summarize((a.directory/'native-metal.log').read_text(),json.loads((a.directory/'benchmark.json').read_text()))
    (a.directory/'gpu-timeline.json').write_text(json.dumps(r,indent=2)+'\n');print(json.dumps(r,indent=2))
