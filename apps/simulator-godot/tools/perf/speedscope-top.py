#!/usr/bin/env python3
"""speedscope_top.py FILE [N] [THREAD_SUBSTR] [ROOT_SUBSTR]
On-CPU (CPU_TIME leaf) profile of dotnet-trace thread-time speedscope output: self = innermost managed frame,
inclusive = any frame on the stack. THREAD_SUBSTR picks threads ('main' = the longest-span thread).
ROOT_SUBSTR restricts to samples whose stack contains a frame with that substring."""
import json, sys, collections
f = sys.argv[1]; N = int(sys.argv[2]) if len(sys.argv) > 2 else 40
flt = sys.argv[3] if len(sys.argv) > 3 else None
root = sys.argv[4] if len(sys.argv) > 4 else None
d = json.load(open(f))
frames = [fr['name'] for fr in d['shared']['frames']]
def short(n): return n.split('!')[-1].split('(')[0][:150]
PSEUDO = {'CPU_TIME', 'UNMANAGED_CODE_TIME'}
profiles = d['profiles']
def cpu_of(p):
    stack = []; last = None; tot = 0.0
    for ev in p['events']:
        t = ev['at']
        if last is not None and stack and frames[stack[-1]] == 'CPU_TIME': tot += t - last
        last = t
        if ev['type'] == 'O': stack.append(ev['frame'])
        elif stack and stack[-1] == ev['frame']: stack.pop()
        elif ev['frame'] in stack: stack.remove(ev['frame'])
    return tot
if flt == 'main':
    profiles = [max(profiles, key=cpu_of)]
elif flt and flt != 'all':
    profiles = [p for p in profiles if flt in p.get('name', '')]
selfc = collections.Counter(); selfu = collections.Counter(); incl = collections.Counter(); tot = collections.Counter()
for p in profiles:
    stack = []; last = None
    for ev in p['events']:
        t = ev['at']
        if last is not None and stack:
            dt = t - last
            names = [frames[i] for i in stack]
            if root is None or any(root in n for n in names):
                leaf = names[-1]
                if leaf in PSEUDO:
                    real = [n for n in names if n not in PSEUDO]
                    me = real[-1] if real else '?'
                    if leaf != 'CPU_TIME':
                        app = [n for n in real if ('MarvinGodot!' in n or 'MarvinCore!' in n)]
                        me = (app[-1] if app else me) + ' -> ' + short(real[-1] if real else '?')
                    (selfc if leaf == 'CPU_TIME' else selfu)[me] += dt
                    tot[leaf] += dt
                    if leaf == 'CPU_TIME':
                        for n in set(real): incl[n] += dt
        last = t
        if ev['type'] == 'O': stack.append(ev['frame'])
        else:
            if stack and stack[-1] == ev['frame']: stack.pop()
            elif ev['frame'] in stack: stack.remove(ev['frame'])
cpu = tot['CPU_TIME']
print(f"threads {len(profiles)}  managed CPU {cpu:.0f} ms  unmanaged/blocked {tot['UNMANAGED_CODE_TIME']:.0f} ms")
print("== self (managed on-CPU)")
for k, v in selfc.most_common(N): print(f"{v:9.1f} {100*v/max(cpu,1):5.1f}%  {short(k)}")
print("== inclusive (managed on-CPU)")
for k, v in incl.most_common(N): print(f"{v:9.1f} {100*v/max(cpu,1):5.1f}%  {short(k)}")
print("== unmanaged time by calling managed frame")
for k, v in selfu.most_common(25): print(f"{v:9.1f}  {short(k.split(' -> ')[0])} -> {k.split(' -> ')[-1]}")
