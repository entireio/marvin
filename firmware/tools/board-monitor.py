#!/usr/bin/env python3
"""USB diagnostic console: logs only text/status, never captures raw microphone audio."""
import argparse,datetime,os,threading
from pathlib import Path
import serial
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',default='/dev/cu.usbmodem1101');a=p.parse_args()
os.umask(0o077)
root=Path(__file__).resolve().parents[2];log=root/'work/board'/('monitor-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.txt')
stop=threading.Event()
with serial.Serial(a.port,115200,timeout=.2) as port,log.open('x') as output:
 def reader():
  while not stop.is_set():
   try:
    data=port.read(4096)
    if data:
     text=data.decode('utf-8',errors='replace');print(text,end='',flush=True);output.write(text);output.flush()
   except serial.SerialException:
    print('USB disconnected. Reopen this monitor after reconnecting.');stop.set();return
 thread=threading.Thread(target=reader,daemon=True);thread.start()
 print(f'Logging diagnostic text to {log}\nEnter t for a quiet tone, m for microphone levels, q to close.\nIf no boot status appears, press the board RESET button once.',flush=True)
 try:
  while not stop.is_set():
   command=input().strip().lower()
   if command=='q':break
   if command in ('t','m'):port.write(command.encode());port.flush()
   else:print('Use t, m, or q.')
 except (KeyboardInterrupt,EOFError):pass
 finally:stop.set();thread.join(timeout=2)
