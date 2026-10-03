import json,sys,importlib.util,statistics,bisect,xml.etree.ElementTree as E
from pathlib import Path
raw=Path(sys.argv[1]);prefix=sys.argv[2];native=Path(sys.argv[3]);root=Path('/Users/thomi/Projects/marvee-perf-phase-1-baseline')
spec=importlib.util.spec_from_file_location('overview',root/'scripts/rendering/read-game-overview.py');ov=importlib.util.module_from_spec(spec);spec.loader.exec_module(ov)
r=json.loads((native/'benchmark.json').read_text());xml=E.parse(raw/f'{prefix}-poi.xml').getroot();ids={x.attrib['id']:x for x in xml.iter() if 'id' in x.attrib}
def resolve(x):return ids[x.attrib['ref']] if 'ref' in x.attrib else x
def text(x):
 x=resolve(x)
 return x.text or ''.join(text(c) for c in x)
events=[];seen=set();cols=None
for node in xml.findall('node'):
 schema=node.find('schema')
 if schema is not None:cols=[x.findtext('mnemonic') for x in schema.findall('col')]
 for row in node.findall('row'):
  v=dict(zip(cols,map(resolve,row)));name=text(v['name'])
  if name not in ['TownBenchmarkStart','TownBenchmarkEnd']:continue
  message=resolve(v['message']);parts=[resolve(x) for x in message];uuid=[text(x) for x in parts if x.tag=='string'][0];numbers=[float(text(x)) for x in parts if x.tag=='fixed-decimal'];ts=int(text(v['time']))/1e9;key=(ts,name,uuid,*numbers)
  if key in seen:continue
  seen.add(key);events.append({'name':name,'traceSeconds':ts,'runID':uuid,'markerUptime':numbers[0],'benchmarkBoundaryUptime':numbers[1],'clockOriginUptime':numbers[0]-ts,'pid':int(text(resolve(v['process']).find('pid')))})
starts=[x for x in events if x['name']=='TownBenchmarkStart'];ends=[x for x in events if x['name']=='TownBenchmarkEnd'];assert len(ends)==1,'Missing or ambiguous end marker';assert all(x['runID']==r['benchmarkRunID'] for x in events),'Wrong run UUID';assert all(abs(x['benchmarkBoundaryUptime']-r['startUptime'])<1e-6 for x in starts),'Wrong start boundary';assert abs(ends[0]['benchmarkBoundaryUptime']-r['startUptime']-r['durationSeconds'])<1e-6,'Wrong end boundary'
origin=statistics.median(x['clockOriginUptime'] for x in events);spread=max(x['clockOriginUptime'] for x in events)-min(x['clockOriginUptime'] for x in events);a=r['startUptime']+3-origin;b=a+600
xml=E.parse(raw/f'{prefix}-layer-count.xml').getroot();ids2={x.attrib['id']:x for x in xml.iter() if 'id' in x.attrib};names={};cols=None
for node in xml.findall('node'):
 schema=node.find('schema')
 if schema is not None:cols=[x.findtext('mnemonic') for x in schema.findall('col')]
 for row in node.findall('row'):
  v={k:ids2[x.attrib['ref']] if 'ref' in x.attrib else x for k,x in zip(cols,row)}
  names[v['layer-id'].text]=v['layer-name'].text
groups=ov.read_intervals(raw/f'{prefix}-intervals.xml');candidates=[(k,v) for k,v in groups.items() if k[3].endswith('.On Display') and names.get(k[2])=='SCNMetalBackingLayer: MarvinSimulator.SimulatorView'];assert len(candidates)==1,'Gameplay layer ambiguous';key,rows=candidates[0];assert all(x['pid']==int(key[0]) for x in events),'PID mismatch';rows.sort(key=lambda x:x['start']);ns_a=a*1e9;ns_b=b*1e9;st=[x['start'] for x in rows];ivals=[y['start']-x['start'] for x,y in zip(rows,rows[1:])];sample=[v for i,v in enumerate(ivals) if ns_a<=st[i]<ns_b];frames=[x['frame'] for x in rows]
minutes=[{'minute':i+1,'displayStartsPerSecond':(bisect.bisect_left(st,ns_a+(i+1)*60e9)-bisect.bisect_left(st,ns_a+i*60e9))/60,'intervals':ov.distribution([v for j,v in enumerate(ivals) if ns_a+i*60e9<=st[j]<ns_a+(i+1)*60e9])} for i in range(10)]
roll=[{'startMeasuredSecond':i,'displayStartsPerSecond':(bisect.bisect_left(st,ns_a+(i+10)*1e9)-bisect.bisect_left(st,ns_a+i*1e9))/10} for i in range(591)]
result={'runID':r['benchmarkRunID'],'pid':int(key[0]),'layerID':key[2],'layerName':names[key[2]],'events':events,'clockOffsetSpreadMicroseconds':spread*1e6 if len(events)>1 else None,'bothMarkersRetained':bool(starts),'clockAlignmentMethod':'multiple retained POI anchors' if starts else 'single retained end POI anchor; no independent whole-window offset consistency check','measuredTraceWindowSeconds':[a,b],'measuredSeconds':600,'drawableDimensions':[r['drawableWidth'],r['drawableHeight']],'runtimeAO':r.get('ambientOcclusion'),'quality':r['quality'],'firstDisplayStartSeconds':st[0]/1e9,'lastDisplayStartSeconds':st[-1]/1e9,'frameIDsContiguous':frames==list(range(frames[0],frames[0]+len(frames))),'displayStartsBracketWindow':st[0]<=ns_a and st[-1]>=ns_b,'distribution':ov.distribution(sample),'minutes':minutes,'worstRolling10Seconds':min(roll,key=lambda x:x['displayStartsPerSecond']),'envelopes':[{**dict(zip(['pid','process','layer','metric'],k)),'distribution':ov.distribution([x['duration'] for x in v if ns_a<=x['start']<ns_b])} for k,v in groups.items() if k[2]==key[2] and not k[3].endswith('.On Display')],'limitations':['Instrumented run only; not clean-run presentation.','GPU Begin to End and CPU Begin to Present are elapsed envelopes, not exclusive GPU busy or CPU execution.','No independent lost-event detector; contiguous layer frame IDs and full bracketing provide continuity checks. Missing start POI remains an unresolved collection limitation.','Quantiles use the existing diagnostic parser; rolling windows step one second.']}
result['identityAndCoverageVerified']=result['frameIDsContiguous'] and result['displayStartsBracketWindow'] and bool(starts) and spread<.0001
(raw/f'{prefix}-validation.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
