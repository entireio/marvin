"""Offline character sound design from licensed vocal performances and workshop foley.
Usage: python characters.py SOURCE_DIRECTORY PREVIEW_DIRECTORY
Requires numpy, scipy, ffmpeg. Sources and research are documented in docs/race-audio.md.
No film audio is used. This renders audition candidates; signal tests cannot judge resemblance.
"""
import pathlib, subprocess, sys, wave
import numpy as np
from scipy import signal, ndimage

RATE = 48000
ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT/'apps/simulator-macos/Resources/Audio'
SOURCE = pathlib.Path(sys.argv[1])
PREVIEW = pathlib.Path(sys.argv[2]); PREVIEW.mkdir(parents=True, exist_ok=True)
MOODS = ['acknowledge', 'effort', 'startle', 'overtake', 'passed']
CHARACTERS = ['marvin', 'r2d2', 'bb8', 'wallE']

def read(path):
    return np.frombuffer(subprocess.check_output(['ffmpeg','-v','error','-i',str(path),'-ar',str(RATE),'-ac','1','-f','f32le','-']),np.float32).astype(float)

def norm(x, rms=.18, peak=.7):
    x=x-np.mean(x)
    return x*min(rms/max(1e-8,np.sqrt(np.mean(x*x))),peak/max(1e-8,np.max(abs(x))))

def fade(x, seconds=.015):
    x=x.copy();n=min(len(x)//2,int(RATE*seconds));r=np.sin(np.linspace(0,np.pi/2,n))**2
    x[:n]*=r;x[-n:]*=r[::-1];return x

def save(path,x):
    assert np.isfinite(x).all() and np.max(abs(x))<1
    with wave.open(str(path),'wb') as f:
        f.setparams((1,2,RATE,0,'NONE','not compressed'));f.writeframes((x*32767).round().astype('<i2').tobytes())

def filt(x,lo,hi):
    return signal.sosfilt(signal.butter(3,[lo,hi],btype='bandpass',fs=RATE,output='sos'),x)

def stretch(x,duration):
    return signal.resample(x,max(1,int(duration*RATE)))

def loop(x,seconds=6):
    # Crossfade a source into itself; after removing the leading overlap the seam is continuous.
    n=min(int(.12*RATE),len(x)//4);r=np.linspace(0,1,n)
    x=x.copy();x[-n:]=x[-n:]*(1-r)+x[:n]*r;x=x[n:]
    return x

vocal=read(SOURCE/'effort.wav')
# Individual recorded efforts, including quieter attacks and vocal release.
centers=[.32,.9,1.65,2.56,4.37,5.25,6.1,6.68,7.77,8.9,10.2,11.6]
syllables=[norm(fade(vocal[int((c-.16)*RATE):int((c+.25)*RATE)]),.22) for c in centers]

def performance(index,duration):
    x=stretch(syllables[index%len(syllables)],duration)
    envelope=np.sqrt(ndimage.uniform_filter1d(x*x,481))
    envelope/=max(envelope.max(),1e-8)
    envelope=np.maximum(envelope-.025,0)
    return x,envelope

def contour(x,envelope,base):
    # Follow recorded vocal pitch rather than replaying a three-note tune.
    pitches=[];hop=480;window=2048
    for offset in range(0,len(x),hop):
        a=x[offset:offset+window]
        if len(a)<window:a=np.pad(a,(0,window-len(a)))
        a=(a-a.mean())*np.hanning(window)
        corr=signal.fftconvolve(a,a[::-1],mode='full')[window-1:]
        low,high=100,700
        lag=low+np.argmax(corr[low:high]);pitches.append(RATE/lag)
    p=ndimage.median_filter(np.array(pitches),size=5)
    p=np.interp(np.arange(len(x)),np.arange(len(p))*hop,p)
    p=np.clip(p/np.median(p),.7,1.5)
    return base*p

def talkbox(modulator,carrier,shift=1):
    # Analysis/synthesis filter bank transfers a performed mouth's changing spectrum
    # onto the electronic carrier. This is a vocoder approximation of a talkbox.
    result=np.zeros(len(carrier));edges=np.geomspace(130,6500,23)
    for lo,hi in zip(edges,edges[1:]):
        band=filt(modulator,lo,hi)
        env=np.sqrt(ndimage.uniform_filter1d(band*band,481))
        c=filt(carrier,lo*shift,min(15000,hi*shift))
        result+=c*env/(np.sqrt(np.mean(c*c))+1e-5)
    return norm(result,.2)

def phrase(robot,mood,variant):
    rng=np.random.default_rng(510+CHARACTERS.index(robot)*311+MOODS.index(mood)*71+variant)
    count=[2,4,3,1,3,5][variant]
    if mood=='startle':count=1+variant%2
    result=[]
    for j in range(count):
        duration=float(rng.uniform(.16,.42))
        if mood=='startle':duration*=1.25
        if robot=='wallE':duration*=1.15
        x,env=performance(variant*3+j*5+MOODS.index(mood)*2,duration)
        t=np.arange(len(x))/RATE;u=np.linspace(0,1,len(x))
        base={'r2d2':1250,'bb8':440,'wallE':190,'marvin':260}[robot]*rng.uniform(.8,1.25)
        f=contour(x,env,base)
        # Distinct inflections for addressing a passed player vs responding to being passed.
        bend={'acknowledge':1.22,'effort':.82,'startle':.45,'overtake':1.5,'passed':.60}[mood]
        f*=np.exp(np.log(bend)*u)
        phase=2*np.pi*np.cumsum(f)/RATE
        if robot=='r2d2':
            # Alternate whistles, stepped chirps, trills and pitched vocal rasps.
            if (j+variant)%3==0:
                steps=np.interp(u,[0,.16,.25,.48,.64,1],[1,1,1.4,1.4,.8,.93])
                phase=2*np.pi*np.cumsum(f*steps)/RATE
            trill=1 if (j+variant)%3!=1 else (.62+.38*np.sin(2*np.pi*(22+variant*3)*t))
            whistle=np.sin(phase+.17*np.sin(phase*2))*env*trill
            voice=np.interp(np.arange(len(x))*2.3,np.arange(len(x)),x,left=0,right=0)
            rasp=talkbox(x,signal.sawtooth(phase,width=.5),1.65)
            y=.62*norm(whistle,.2)+.25*rasp+.13*norm(voice,.2)
        else:
            # Rich carrier shaped by recorded syllables, not static synthetic vowel bands.
            carrier=.75*signal.sawtooth(phase,width=.42)+.25*np.sin(phase*1.007)
            y=talkbox(x,carrier,{'bb8':1.15,'wallE':.85,'marvin':1.02}[robot])
            if robot=='bb8':
                y=.88*y+.12*np.sin(phase)*env
            elif robot=='wallE':
                y=.76*y+.24*np.tanh(x*2)*env
                y=filt(y,120,4500)
            else:
                y=.88*y+.12*np.sin(phase*.5)*env
                y=filt(y,170,4200)
        result.extend([fade(norm(y,.18)),np.zeros(int(RATE*rng.uniform(.025,.13)))])
    return np.concatenate([np.zeros(960),*result,np.zeros(2400)])

workshop=SOURCE/'workshop'
drill=read(workshop/'workshop - drill long.wav')[int(.45*RATE):int(3.9*RATE)]
machine=read(workshop/'workshop - machine.wav')[3*RATE:10*RATE]
ratchet=read(workshop/'workshop - ratchet1.wav')
scrape=read(workshop/'workshop - quiet scrape.wav')[int(.4*RATE):int(4.6*RATE)]
# Recorded continuous mechanisms; no sustained oscillator hums in driving assets.
for i,name in enumerate(CHARACTERS):
    source=drill if name in ['marvin','r2d2'] else machine
    speed={'marvin':.70,'r2d2':1.05,'bb8':.82,'wallE':.57}[name]
    motor=signal.resample(source,int(len(source)/speed))
    motor=filt(motor,{'marvin':110,'r2d2':230,'bb8':75,'wallE':95}[name],{'marvin':2600,'r2d2':3400,'bb8':1000,'wallE':2200}[name])
    motor=norm(loop(motor),.12,.48)
    save(OUT/f'{name}.wav',motor)
    ground=ratchet if name in ['marvin','wallE'] else scrape
    ground=signal.resample(ground,int(len(ground)/(1.5 if name=='marvin' else .95 if name=='wallE' else 1.1)))
    ground=norm(loop(filt(ground,100,1800 if name=='bb8' else 3500)),.10,.42)
    save(OUT/f'{name}-ground.wav',ground)
    preview=[]
    for mood in MOODS:
        for variant in range(6):
            y=phrase(name,mood,variant);save(OUT/f'{name}-{mood}-{variant}.wav',y)
        preview.extend([phrase(name,mood,MOODS.index(mood)),np.zeros(int(.7*RATE))])
    save(PREVIEW/f'{name}-voice.wav',np.concatenate(preview))
    # Isolated movement: rest, accelerate, cruise, brake, rest. Same gain/pitch law as runtime.
    t=np.arange(RATE*10)/RATE;motion=np.interp(t,[0,1,4,6,9,10],[0,0,1,1,0,0])
    motorPhase=np.cumsum(.82+motion*.50)*1.0;groundPhase=np.cumsum(.6+motion*.9)
    m=np.interp(motorPhase%len(motor),np.arange(len(motor)),motor)
    g=np.interp(groundPhase%len(ground),np.arange(len(ground)),ground)
    mix=m*(.18*motion)+g*(.13*motion)
    save(PREVIEW/f'{name}-drive.wav',mix*.65)
print('Rendered 120 performed voice variants, eight recorded driving layers, and isolated previews.')
