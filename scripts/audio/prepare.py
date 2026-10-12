"""Offline licensed crowd mastering. No runtime downloads.
Usage: python3 scripts/audio/prepare.py /path/to/downloaded-source-directory
Source directory contains cheers.ogg and the extracted Gregor Quendel crowd archive.
"""
import array, math, pathlib, random, subprocess, sys, wave
root=pathlib.Path(__file__).resolve().parents[2]
out=root/'apps/simulator-macos/Resources/Audio';out.mkdir(exist_ok=True)
rate=48000

def write(name, samples, crossfade=.10):
    n=int(crossfade*rate)
    for i in range(n):
        t=i/n;samples[-n+i]=samples[-n+i]*(1-t)+samples[i]*t
    samples=samples[n:]
    peak=max(abs(x) for x in samples)
    samples=[x*.65/max(.001,peak) for x in samples]
    with wave.open(str(out/(name+'.wav')),'wb') as w:
        w.setparams((1,2,rate,0,'NONE','not compressed'))
        w.writeframes(array.array('h',(round(x*32767) for x in samples)).tobytes())

 # Character rendering has its own source directory; run characters.py separately.

source=pathlib.Path(sys.argv[1])
def decode(path):
    data=subprocess.check_output(['/opt/homebrew/bin/ffmpeg','-v','error','-i',str(path),'-t','26','-af','highpass=f=120,lowpass=f=6500','-ar',str(rate),'-ac','1','-f','f32le','-'])
    return list(array.array('f',data))
recording=next(source.rglob('*07 - Soft cheering and chatter.mp3'))
write('crowd',decode(recording),1.0)
cheer=decode(source/'cheers.ogg')
sparse=[0.0]*(rate*23)
for start,level in [(3,.5),(14,.35)]:
    for i,x in enumerate(cheer[:rate*4]):sparse[start*rate+i]+=x*level
write('sparse-crowd',sparse,.1)

# Remove the recording's long fade-in/out: a finish must get an immediate crowd response.
write('finish-crowd',decode(next(source.rglob('*03 - Strong cheering - I.mp3')))[5*rate:16*rate],.7)
