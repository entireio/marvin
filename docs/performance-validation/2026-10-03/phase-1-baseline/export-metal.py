import subprocess,concurrent.futures,json
from pathlib import Path
root=Path('/Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03')
schemas=['metal-gpu-intervals','metal-application-encoders-list','metal-application-command-buffer-submissions','metal-current-allocated-size','gpu-performance-state-intervals','device-thermal-state-intervals','time-profile']
def work(item):
 name,schema=item
 command=['xcrun','xctrace','export','--input',str(root/f'metal-{name}.trace'),'--xpath',f'/trace-toc/run[@number="1"]/data/table[@schema="{schema}"]','--output',str(root/f'metal-{name}-{schema}.xml')]
 r=subprocess.run(command,capture_output=True,text=True)
 return dict(name=name,schema=schema,command=command,returncode=r.returncode,output=r.stdout+r.stderr)
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
 results=list(pool.map(work,[(n,s) for n in ['early','loaded'] for s in schemas]))
(root/'metal-export-results.json').write_text(json.dumps(results,indent=2)+'\n')
for r in results: print(r['name'],r['schema'],r['returncode'],r['output'][:180],flush=True)
