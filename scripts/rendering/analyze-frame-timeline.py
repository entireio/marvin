#!/usr/bin/env python3
"""Correlate callback gaps with simulation stages; these are not GPU timestamps."""
import argparse,bisect,json,statistics
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);args=p.parse_args()
r=json.loads((args.directory/'benchmark.json').read_text());t=json.loads((args.directory/'timeline.json').read_text())
u=t['updates'];f=t['renderFrames'];times=[x[0] for x in u]
def stats(values):
 v=sorted(values)
 return {k:round(v[min(len(v)-1,int((len(v)-1)*q))],3) for k,q in [('p50',.5),('p95',.95),('p99',.99),('max',1)]} if v else {}
events=[]
for frame in f:
 end,interval,span=frame[:3]
 if interval<=25:continue
 window=u[bisect.bisect_left(times,end-interval/1000-.02):bisect.bisect_right(times,end+.002)]
 if not window:continue
 latest=window[-1]
 events.append({'elapsed':round(end-r['startUptime'],3),'intervalMS':round(interval,3),'renderSpanMS':round(span,3),'rendererCycleMS':round(frame[3],3) if len(frame)>3 else None,'preRenderMS':round(frame[7],3) if len(frame)>7 else None,'sceneAnimationMS':round(frame[4],3) if len(frame)>4 else None,'aerial':bool(latest[11]),'position':[round(latest[9],2),round(latest[10],2)],'maxTickMS':round(max(x[2] for x in window),3),'maxUpdateMS':round(max(x[8] for x in window),3),'maxPhysicsMS':round(max(x[3] for x in window),3)})
report={'meanRenderCallbacksPerSecond':r['meanFPS'],'stagesMS':{name:stats([x[i] for x in u]) for i,name in enumerate(t['updateColumns']) if 2<=i<=8},'rendererStagesMS':{name:stats([x[i] for x in f]) for i,name in enumerate(t['renderColumns']) if i>=2},'gapEvents':events,'limitation':'Callback timestamps and CPU update spans; no GPU-completion or display-present timestamps.'}
(args.directory/'analysis.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
