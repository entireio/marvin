"""Original expressive robot recreations; no film recordings or actor voice samples.
Run offline with Python 3. Produces three variants of acknowledge/effort/startle
for each character, plus revised mechanical loops and a labeled-by-order demo.
"""
import array,math,pathlib,random,wave
root=pathlib.Path(__file__).resolve().parents[2]
out=root/'apps/simulator-macos/Resources/Audio'
rate=48000;TAU=2*math.pi

def save(path,samples,peak=.72):
    top=max(abs(x) for x in samples) or 1
    with wave.open(str(path),'wb') as w:
        w.setparams((1,2,rate,0,'NONE','not compressed'))
        w.writeframes(array.array('h',(round(max(-1,min(1,x/top*peak))*32767) for x in samples)).tobytes())

def phrase(robot,mood,variant):
    rng=random.Random(19+variant+31*['marvin','r2d2','bb8','wallE'].index(robot))
    result=[0.0]*int(rate*.035)
    contours={
        'r2d2':[(.22,1150,1420),(.13,1750,1630),(.45,900,1850)],
        'bb8':[(.36,250,420),(.19,440,330),(.38,300,480)],
        'wallE':[(.53,140,205),(.48,260,210)],
        'marvin':[(.29,270,320),(.16,405,405),(.37,340,300)]
    }[robot]
    if mood=='effort':contours=[(d*.8,a*1.17,b*1.28) for d,a,b in contours]
    if mood=='startle':contours=[(d*.7,b*1.25,a*.75) for d,a,b in contours]
    phase=0;low=0
    for syllable,(duration,a,b) in enumerate(contours):
        a*=1+(variant-1)*.06;b*=1+(variant-1)*.05
        length=int((duration+variant*.013)*rate)
        for i in range(length):
            t=i/rate;u=i/max(1,length-1);curve=u*u*(3-2*u)
            f=(a+(b-a)*curve)*(1+.008*math.sin(TAU*5.3*t)+.003*math.sin(TAU*13.7*t))
            phase+=TAU*f/rate
            envelope=min(1,t/.025)*min(1,(length-i)/rate/.055)*(0.82+.18*math.sin(math.pi*u))
            low+=.08*(rng.uniform(-1,1)-low)
            if robot=='r2d2':
                # Whistle/bleep phrases, fluid upward questions and falling objections.
                fm=.28*math.sin(phase*2+.8*math.sin(TAU*7*t))
                signal=.60*math.sin(phase+fm)+.13*math.sin(phase/2)+.10*math.sin(phase*3)
                signal*=.87+.13*math.sin(TAU*(9+syllable*3)*t)
            else:
                if robot=='bb8':
                    formants=[(410+430*curve,170),(1050+350*math.sin(math.pi*u),240),(2450,360)]
                elif robot=='wallE':
                    # Rounded onset opens to ah, followed by a clear ee-like vowel.
                    formants=[(340+500*curve,180),(750+450*curve,260),(2500,380)] if syllable==0 else [(320,160),(2250,320),(3100,450)]
                else:
                    formants=[(460+110*curve,170),(1300-180*curve,260),(2700,400)]
                signal=0
                for h in range(1,19):
                    amplitude=sum(math.exp(-.5*((h*f-center)/width)**2)*weight for (center,width),weight in zip(formants,[1,.58,.22]))/math.sqrt(h)
                    signal+=amplitude*math.sin(phase*h+.05*math.sin(TAU*23*t))
                signal+=low*.08
                if robot=='bb8':signal*=.72+.28*math.sin(TAU*(4.5+variant)*t+.5*math.sin(TAU*2*t))
                if robot=='wallE':signal=math.tanh(signal*1.7)*(.9+.1*math.sin(TAU*31*t))
                if robot=='marvin':signal=.7*math.tanh(signal)+.22*math.sin(phase*2.73)*math.exp(-t*7)
            result.append(signal*envelope)
        result.extend([0.0]*int(rate*(.045 if robot=='bb8' else .075)))
    result.extend([0.0]*int(rate*.12))
    return result

for name,base in [('marvin',86),('r2d2',174),('bb8',52),('wallE',68)]:
    rng=random.Random(base);samples=[];low=0;phase=0
    for i in range(rate*6):
        t=i/rate;white=rng.uniform(-1,1);low+=.035*(white-low)
        phase+=TAU*base*(1+.004*math.sin(TAU*.7*t))/rate
        if name=='marvin':s=.20*math.sin(phase)+.045*math.sin(phase*3)+low*.5+white*.03*max(0,math.sin(TAU*19*t))**14
        elif name=='r2d2':s=.08*math.sin(phase)+.05*math.sin(phase*4)+low*.35+white*.045*(.5+.5*math.sin(TAU*27*t))
        elif name=='bb8':s=low*1.7+.06*math.sin(phase)+.035*math.sin(phase*5.9)+low*.25*math.sin(TAU*5*t)
        else:s=low*.7+.09*math.sin(phase)+.04*math.sin(phase*2.3)+white*.18*max(0,math.sin(TAU*13*t+.3*math.sin(TAU*2*t)))**18
        samples.append(s)
    n=int(.2*rate)
    for i in range(n):samples[-n+i]=samples[-n+i]*(1-i/n)+samples[i]*(i/n)
    save(out/(name+'.wav'),samples[n:],.50)
    for mood in ['acknowledge','effort','startle']:
        for variant in range(3):save(out/f'{name}-{mood}-{variant}.wav',phrase(name,mood,variant))

# Isolated voices, with two seconds of breathing room between characters.
# Order: R2-D2, BB-8, WALL-E, Marvin. Three emotional phrases per character.
demo=[]
for name in ['r2d2','bb8','wallE','marvin']:
    for j,mood in enumerate(['acknowledge','effort','startle']):
        data=phrase(name,mood,j);scale=.65/max(abs(x) for x in data)
        demo.extend(x*scale for x in data);demo.extend([0.0]*int(rate*.7))
    demo.extend([0.0]*rate)
save(pathlib.Path('/tmp/robot-voice-comparison.wav'),demo,.65)
