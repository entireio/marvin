"""Rebuild the mechanical/ambient mix from licensed recordings and original DSP.
Run after characters.py: soundscape.py SOURCE_DIRECTORY. Requires numpy/scipy/ffmpeg.
Workshop and market source URLs/hashes are in sources.json. Deterministic output.
"""
import pathlib, subprocess, sys, wave
import numpy as np
from scipy import signal, ndimage
RATE=48000
OUT=pathlib.Path(__file__).resolve().parents[2]/'apps/simulator-macos/Resources/Audio'
SOURCE=pathlib.Path(sys.argv[1]); rng=np.random.default_rng(20261002)
names=['marvin','r2d2','bb8','wallE']
def read(p):
    return np.frombuffer(subprocess.check_output(['ffmpeg','-v','error','-i',str(p),'-ar',str(RATE),'-ac','1','-f','f32le','-']),dtype='<f4').astype(float)
def band(x,lo,hi):
    return signal.sosfilt(signal.butter(3,[lo,hi],btype='bandpass',fs=RATE,output='sos'),x)
def norm(x,rms=.22,peak=.72):
    x=x-x.mean(); x=np.tanh(x/(np.sqrt(np.mean(x*x))+1e-9)*.6)
    return x*min(rms/(np.sqrt(np.mean(x*x))+1e-9),peak/(abs(x).max()+1e-9))
def fade(x,n=720):
    x=x.copy(); n=min(n,len(x)//2); a=np.sin(np.linspace(0,np.pi/2,n))**2
    x[:n]*=a;x[-n:]*=a[::-1];return x
def loop(x):
    n=min(12000,len(x)//5);a=np.linspace(0,1,n);x=x.copy()
    x[-n:]=x[-n:]*(1-a)+x[:n]*a
    return x[n:]
def save(name,x):
    assert np.isfinite(x).all() and abs(x).max()<.95
    with wave.open(str(OUT/(name+'.wav')),'wb') as f:
        f.setparams((1,2,RATE,0,'NONE','not compressed'));f.writeframes(np.round(x*32767).astype('<i2').tobytes())
def fit(x,n,rate=1):
    return np.interp((np.arange(n)*rate)%len(x),np.arange(len(x)),x)
def workshop(name):return read(SOURCE/'workshop'/('workshop - '+name+'.wav'))
machine=workshop('machine')[3*RATE:10*RATE]
scrape=workshop('quiet scrape')
motor=read(SOURCE/'electric-motor.mp3')
scooter=read(SOURCE/'electric-scooter.mp3')
# Sustained electromechanical recordings replace the rejected drill and ratchet.
# Smooth gain before looping: source throttle changes must not fight game input.
def level(x,rms=.22):
    x=steady(x);return x*min(rms/(np.sqrt(np.mean(x*x))+1e-9),.72/(abs(x).max()+1e-9))
def steady(x):
    envelope=np.sqrt(ndimage.gaussian_filter1d(x*x,RATE*.08)+1e-9)
    return x/np.maximum(envelope,1e-6)
motor=steady(motor[int(.6*RATE):int(10.8*RATE)])
scooter=steady(scooter[int(.55*RATE):int(1.75*RATE)])
def clean(x,lo,hi,rms):
    # Linear mastering preserves machinery detail without saturation harmonics.
    x=band(x,lo,hi);x-=x.mean()
    return x*min(rms/(np.sqrt(np.mean(x*x))+1e-9),.72/(abs(x).max()+1e-9))
for i,name in enumerate(names):
    n=RATE*9;t=np.arange(n)/RATE
    core=fit(motor,n,[.67,1.08,.43,.53][i])
    wheel=fit(scooter,n,[.55,.83,.39,.42][i])
    low=clean(core,45,[800,1150,500,700][i],.17)+clean(wheel,55,420,.06)
    high=clean(core,85,[1250,1700,800,1050][i],.17)+clean(wheel,100,[950,1400,600,800][i],.07)
    # WALL-E carries low irregular tread weight; BB-8 remains a smooth roller.
    if i==3:
        flutter=.94+.035*np.sin(2*np.pi*13*t)+.025*np.sin(2*np.pi*21.3*t)
        low*=flutter;high*=flutter
    save(name,loop(level(clean(low,40,[390,480,300,360][i],.22))))
    save(name+'-high',loop(level(clean(high,[440,540,340,405][i],2200,.22))))
    # Ground is subdued rolling granularity, with no scraping/ratchet recording.
    grit=band(rng.normal(size=n),80,[900,1000,650,850][i])
    envelope=ndimage.gaussian_filter1d(rng.uniform(size=n),RATE*.004)
    contact=grit*(.5+envelope)
    if i==3: contact*=.72+.28*np.sin(2*np.pi*17*t)**2
    save(name+'-ground',loop(clean(contact,65,1200,.075)))
    sand=band(rng.normal(size=n),130,1700)
    save(name+'-sand',loop(clean(sand,110,1800,.12)))
    # Boost is a broad pressure rush with a low mechanical body. No drill,
    # narrow squeal, periodic tremolo or hard onset; hold/release share timbre.
    air=clean(rng.normal(size=n),55,[1350,1750,1100,1250][i],.20)
    body=clean(fit(motor,n,[.49,.68,.35,.41][i]),40,420,.07)
    boost=air+body
    save(name+'-boost',loop(clean(boost,40,2100,.23)))
    for action,duration in [('boost-on',.38),('boost-off',.62)]:
        m=int(duration*RATE);u=np.linspace(0,1,m)
        shape=np.sin(np.pi*u)**2*np.exp(-u*(1.4 if action=='boost-on' else 2.2))
        y=clean(rng.normal(size=m),65,1500 if action=='boost-on' else 850,.22)
        y+=clean(fit(motor,m,.4),40,350,.035)
        save(name+'-'+action,fade(y*shape,960))
if '--mechanical-only' in sys.argv:
    print('Rendered replacement drivetrains, restrained rolling contact and pressure boost.');sys.exit(0)
# Long unsynchronized ambience loops; no short white-noise repetition.
n=RATE*29;t=np.arange(n)/RATE
wind=band(rng.normal(size=n),45,1300)
gust=.62+.18*np.sin(2*np.pi*t/9.7)+.12*np.sin(2*np.pi*t/5.3+.9)
save('desert-wind',norm(loop(wind*gust),.14))
market=read(SOURCE/'market.mp3')
# Three softly overlapping field-recording perspectives suppress isolated foreground words.
market=sum(fit(market,RATE*27,r)*g for r,g in [(1,.6),(.963,.25),(1.031,.2)])
save('market',norm(loop(band(market,180,3700)),.19))
# Real workshop hits arranged sparsely with quiet machinery underneath.
y=norm(fit(machine,RATE*31,.62),.025)
for k,at in enumerate([1.2,2.0,4.7,8.2,8.6,13.1,17.6,19.9,24.7,27.1]):
    hit=fade(workshop(['low hammering','tool rummaging','clink','dull hammering'][k%4]))
    hit=norm(band(hit,120,3400),.16)*rng.uniform(.55,.95); start=int(at*RATE)
    end=min(len(y),start+len(hit));y[start:end]+=hit[:end-start]
save('workshop',norm(loop(y),.16))
# Muffled cantina walla, crockery and an original sparse hand-played percussion motif.
y=norm(band(fit(market,RATE*32,.97),160,1800),.10)
for k in range(50):
    start=int((k*.61+rng.uniform(-.035,.035)+.2)*RATE)
    hit=workshop('wood on wood thuds' if k%4 in (0,3) else 'dull ping')
    hit=fade(band(hit[:min(len(hit),RATE//3)],140,2400))
    hit=norm(hit,.08)*(.7 if k%4==0 else .3)
    end=min(len(y),start+len(hit));y[start:end]+=hit[:end-start]
save('cantina',norm(loop(y),.17))
# Chassis collisions and countdown cues are short mechanical/UI events, never speech.
impact=fade(band(workshop('wood on wood thuds')[:RATE//3]+0,60,2100))
save('impact',norm(impact,.21))
for name,freq,duration in [('countdown',520,.14),('go',780,.32),('finish',660,.55)]:
    t=np.arange(int(RATE*duration))/RATE
    y=(np.sin(2*np.pi*freq*t)+.3*np.sin(2*np.pi*freq*1.5*t))*np.exp(-t*9)
    save(name,fade(norm(y,.2)))
# Voices retain expression but are short accents, with brittle upper frequencies controlled.
# Always run characters.py first so this mastering is reproducible, not cumulative.
for name in names:
    for mood in ['acknowledge','effort','startle','overtake','passed']:
        for variant in range(6):
            key=f'{name}-{mood}-{variant}';x=read(OUT/(key+'.wav'))
            x=x[:int(RATE*(.85 if mood in ['effort','startle'] else 1.15))]
            save(key,fade(norm(band(x,130,3300 if name=='r2d2' else 2500),.14),1440))
print('Rendered four distinct layered drivetrains, boost on/hold/off, surfaces, localized ambience and restrained voices.')
# Storm layers: low turbulent pressure and dry high-frequency grit, mixed independently.
n=RATE*37;t=np.arange(n)/RATE
pressure=band(rng.normal(size=n),28,650)
pressure*=.65+.16*np.sin(2*np.pi*t/7.3)+.13*np.sin(2*np.pi*t/3.9)
save('storm-gust',norm(loop(pressure),.24))
grit=band(rng.normal(size=n),1600,6500)*.08
for at in rng.uniform(.3,36,135):
    start=int(at*RATE);m=int(rng.uniform(.012,.12)*RATE);end=min(n,start+m)
    burst=band(rng.normal(size=end-start),1800,7200)*np.exp(-np.linspace(0,6,end-start))
    grit[start:end]+=burst*rng.uniform(.2,.65)
save('storm-grit',norm(loop(grit),.12))
