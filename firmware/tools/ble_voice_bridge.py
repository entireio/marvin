"""macOS local BLE-to-WebSocket bridge for a Marvin Pet.

Run this on the same Mac as the local server. The bridge owns the CoreBluetooth
connection, so browser tabs are not part of the real-time voice path. It
expects the Pet's BLE Voice service introduced by the matching firmware.
"""
import argparse, asyncio, json, os
from bleak import BleakClient, BleakScanner
import websockets

SERVICE="0000ff60-0000-1000-8000-00805f9b34fb"
UP_CONTROL=SERVICE.replace("ff60","ff61")
DOWN_CONTROL=SERVICE.replace("ff60","ff62")
UP_AUDIO=SERVICE.replace("ff60","ff63")
DOWN_AUDIO=SERVICE.replace("ff60","ff64")

class Frames:
 def __init__(self): self.pending=None
 def split(self,kind,sequence,payload):
  if not payload or len(payload)>4096: raise ValueError("invalid BLE message")
  size=len(payload); total=(size+227)//228
  return [b"MBL1"+bytes([kind])+sequence.to_bytes(2,"big")+bytes([part,total])+size.to_bytes(2,"big")+b"\0"+payload[part*228:(part+1)*228] for part in range(total)]
 def append(self,frame):
  if len(frame)<13 or len(frame)>240 or frame[:4]!=b"MBL1" or frame[11]: raise ValueError("invalid BLE frame")
  kind,sequence,part,total=frame[4],int.from_bytes(frame[5:7],"big"),frame[7],frame[8]; size=int.from_bytes(frame[9:11],"big")
  if kind not in (1,2) or not total or part>=total or not size or size>4096: raise ValueError("invalid BLE header")
  if self.pending is None:
   if part: raise ValueError("missing initial BLE fragment")
   self.pending=[kind,sequence,total,size,[]]
  current=self.pending
  if current[:4] != [kind,sequence,total,size] or len(current[4]) != part: raise ValueError("reordered BLE fragment")
  current[4].append(bytes(frame[12:]))
  if len(current[4]) != total: return None
  payload=b"".join(current[4]); self.pending=None
  if len(payload)!=size: raise ValueError("truncated BLE message")
  return kind,sequence,payload

async def bridge(args):
 device=await BleakScanner.find_device_by_filter(lambda d,a:SERVICE.lower() in [u.lower() for u in (a.service_uuids or [])],timeout=20)
 if not device: raise RuntimeError("No Marvin BLE Voice service found")
 async with BleakClient(device) as pet, websockets.connect(args.socket,additional_headers={"Authorization":"Bearer "+args.device_token},max_size=65536) as server:
  up,down=Frames(),Frames(); sequence=0; outbound=asyncio.Queue()
  async def send_frames(characteristic,kind,payload):
   nonlocal sequence
   for frame in up.split(kind,sequence,payload): await pet.write_gatt_char(characteristic,frame,response=False)
   sequence=(sequence+1)&0xffff
  def notify(characteristic):
   def received(_,data):
    try:
     message=down.append(bytes(data))
     if message: outbound.put_nowait(message)
    except ValueError: pass
   return received
  await pet.start_notify(DOWN_CONTROL,notify(DOWN_CONTROL)); await pet.start_notify(DOWN_AUDIO,notify(DOWN_AUDIO))
  async def pet_to_server():
   while True:
    kind,_,payload=await outbound.get(); await server.send(payload if kind==2 else payload.decode("utf-8"))
  async def server_to_pet():
   async for message in server:
    if isinstance(message,str): await send_frames(UP_CONTROL,1,message.encode())
    else: await send_frames(UP_AUDIO,2,message)
  await asyncio.gather(pet_to_server(),server_to_pet())

parser=argparse.ArgumentParser();parser.add_argument("--socket",required=True);parser.add_argument("--device-token",default=os.environ.get("MARVIN_DEVICE_TOKEN"));args=parser.parse_args()
if not args.device_token: parser.error("--device-token or MARVIN_DEVICE_TOKEN is required")
asyncio.run(bridge(args))
