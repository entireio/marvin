"""Inspect bundled PCM, independent of the sound generator. Optional JSON output path."""
import json,pathlib,sys,wave
import numpy as np
root=pathlib.Path(__file__).resolve().parents[2]/'apps/simulator-macos/Resources/Audio'
report={};size=0
for p in sorted(root.glob('*.wav')):
    with wave.open(str(p)) as f:
        channels,rate,frames,width=f.getnchannels(),f.getframerate(),f.getnframes(),f.getsampwidth()
        assert width==2,(p,width)
        x=np.frombuffer(f.readframes(frames),dtype='<i2').astype(float)/32768
    size+=frames*2*4 # runtime converts to stereo float
    peak=float(abs(x).max());rms=float(np.sqrt(np.mean(x*x)))
    is_loop=not any(m in p.stem for m in ['acknowledge','effort','startle','overtake','passed','boost-on','boost-off']) and p.stem not in ['impact','countdown','go','finish']
    seam=float(abs(x[0]-x[-1]));p99=float(np.percentile(abs(np.diff(x)),99))
    passed=rate==48000 and peak<.95 and rms>.005 and np.isfinite(x).all()
    if is_loop:passed=passed and seam<max(.025,p99*2)
    else:passed=passed and abs(x[0])<.001 and abs(x[-1])<.001
    report[p.name]={'passed':bool(passed),'peak':peak,'rmsDBFS':20*np.log10(max(rms,1e-9)),'seconds':frames/rate,'seam':seam,'adjacentP99':p99}
result={'passed':all(v['passed'] for v in report.values()),'stereoDecodedMB':size/1e6,'assets':report}
if len(sys.argv)>1:pathlib.Path(sys.argv[1]).write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({'passed':result['passed'],'stereoDecodedMB':result['stereoDecodedMB'],'failed':[k for k,v in report.items() if not v['passed']]}))
sys.exit(0 if result['passed'] else 1)
