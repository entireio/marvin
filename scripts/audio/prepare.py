"""Offline original motor synthesis and licensed crowd mastering. No runtime downloads.
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

for robot,base,pulse,noise in [('marvin',92,22,.15),('r2d2',165,34,.06),('bb8',58,7,.21),('wallE',64,16,.24)]:
    rng=random.Random(400+base);low=0;samples=[]
    for i in range(rate*4):
        t=i/rate;low+=.14*(rng.uniform(-1,1)-low)
        carrier=math.sin(2*math.pi*base*t)+.25*math.sin(2*math.pi*base*2*t)+.08*math.sin(2*math.pi*base*5*t)
        rumble=low*noise+max(0,math.sin(2*math.pi*pulse*t))**12*low*.7
        if robot=='bb8':carrier+=.24*math.sin(2*math.pi*310*t+.6*math.sin(2*math.pi*2*t))
        samples.append(carrier*.16*(.8+.2*math.sin(2*math.pi*pulse*t))+rumble)
    write(robot,samples)

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
