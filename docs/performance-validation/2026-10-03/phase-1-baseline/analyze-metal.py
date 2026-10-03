import json,xml.etree.ElementTree as E,collections,statistics
from pathlib import Path
R=Path('/Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03')
def load(p):
 root=E.parse(p).getroot(); ids={n.get('id'):n for n in root.iter() if n.get('id')}
 def resolve(n):
  while n is not None and n.get('ref'): n=ids[n.get('ref')]
  return n
 def val(n):
  n=resolve(n)
  return n.get('fmt',n.text or '') if n is not None else ''
 return list(root.iter('row')),resolve,val

def stats(a):
 a=sorted(a)
 return {'count':len(a),'median':a[(len(a)-1)//2],'p95':a[int((len(a)-1)*.95)],'p99':a[int((len(a)-1)*.99)],'max':a[-1]} if a else {'count':0}
def union(a):
 a=sorted(a); end=-1; total=0
 for lo,hi in a:
  if hi>end: total+=hi-max(end,lo);end=hi
 return total
out={}
for name in ['early','loaded']:
 d={}; rows,res,val=load(R/f'metal-{name}-metal-application-encoders-list.xml');groups=collections.defaultdict(list); frames=collections.defaultdict(float)
 for row in rows:
  c=list(row)
  if '65595' not in val(c[3]):continue
  duration=float(res(c[1]).text)/1e6
  groups[val(c[8])].append(duration);frames[val(c[5])]+=duration
 d['cpuEncoderWallMSByLabel']={k:stats(v) for k,v in sorted(groups.items())};d['cpuEncoderWallSumPerFrameMS']=stats(list(frames.values()))
 rows,res,val=load(R/f'metal-{name}-metal-gpu-intervals.xml');groups=collections.defaultdict(list);channels=collections.defaultdict(list);unattributed=0; allactive=[]
 for row in rows:
  c=list(row)
  if '65595' not in val(c[10]):unattributed+=1;continue
  if val(c[7])!='Active':continue
  st=float(res(c[0]).text)/1e9;du=float(res(c[1]).text)/1e9
  groups[(val(c[2]),val(c[6]),val(c[5]))].append(du*1000)
  if val(c[5])=='0':channels[val(c[2])].append((st,st+du));allactive.append((st,st+du))
 d['gpuActiveIntervalsByChannelLabelDepth']=[{'channel':k[0],'label':k[1],'depth':k[2],'elapsedMS':stats(v),'sumMS':sum(v)} for k,v in groups.items()]
 d['gpuDepth0ChannelUnionSeconds']={k:union(v) for k,v in channels.items()};d['gpuDepth0AllChannelUnionSeconds']=union(allactive);d['gpuRowsWithoutTargetPID']=unattributed
 rows,res,val=load(R/f'metal-{name}-time-profile.xml');threads=collections.Counter();leaves=collections.Counter();inclusive=collections.Counter();rowcount=0
 for row in rows:
  c=list(row)
  if '65595' not in val(c[2]):continue
  w=float(res(c[5]).text)/1e9;threads[val(c[1])]+=w; stack=res(c[6]);fs=[res(n).get('name','') for n in stack.iter('frame')]
  if fs:leaves[fs[0]]+=w
  for f in set(fs):inclusive[f]+=w
  rowcount+=1
 d['cpuSamples']={'count':rowcount,'weightedSecondsByThread':dict(threads),'topLeavesSeconds':leaves.most_common(15),'topInclusiveSeconds':inclusive.most_common(20)}
 rows,res,val=load(R/f'metal-{name}-metal-current-allocated-size.xml');sz=[int(res(list(row)[4]).text) for row in rows if '65595' in val(list(row)[3])]
 d['metalAllocatedBytes']=stats(sz);d['metalAllocatedFirstLast']=sz[::max(1,len(sz)-1)] if sz else []
 out[name]=d
out['limitations']=['Locked-screen instrumented runs; do not substitute for clean measurements.','GPU interval elapsed durations include nested/overlapping channels. Depth-zero union is recorded activity, not a hardware exclusive-busy counter.','CPU sample weights are statistical, encoding durations are wall spans, generic encoder labels do not identify draw/pipeline costs.']
(R/'metal-analysis.json').write_text(json.dumps(out,indent=2)+'\n')
for name,d in out.items():
 if isinstance(d,dict):print(name,d['cpuEncoderWallSumPerFrameMS'],d['gpuDepth0AllChannelUnionSeconds'],d['metalAllocatedBytes'],d['cpuSamples']['weightedSecondsByThread'])
