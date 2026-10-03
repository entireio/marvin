import json,subprocess,time,sys
from pathlib import Path
pid=sys.argv[1];out=Path(sys.argv[2]);rows=[]
while True:
 p=subprocess.run(['ps','-p',pid,'-o','pid=,%cpu=,rss=,vsz='],capture_output=True,text=True)
 if not p.stdout.strip():break
 v=p.stdout.split()
 rows.append({'monotonicSeconds':time.monotonic(),'wallTime':time.time(),'pid':int(v[0]),'cpuPercent':float(v[1]),'rssKiB':int(v[2]),'virtualKiB':int(v[3]),'systemSwap':subprocess.check_output(['sysctl','-n','vm.swapusage'],text=True).strip()})
 out.write_text(json.dumps({'method':'ps process CPU percent, RSS and VSZ; systemwide vm.swapusage every 15 seconds. Separate native per-second thermal/trail counters. CPU percent 100 equals one core; sampling starts after process launch. Polling overhead not independently measured.','samples':rows},indent=2)+'\n')
 time.sleep(15)
