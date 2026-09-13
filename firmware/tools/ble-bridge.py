"""Private stdin/stdout BLE transport for the TypeScript interoperability test."""
import asyncio,json,sys
from bleak import BleakScanner,BleakClient
SERVICE='0000ff50-0000-1000-8000-00805f9b34fb'
async def main():
 print('BLE: scanning for Marvin',file=sys.stderr,flush=True)
 device=await BleakScanner.find_device_by_filter(lambda d,ad:SERVICE in ad.service_uuids,timeout=20)
 if not device:raise RuntimeError('No Marvin in setup mode')
 print('BLE: device found; connecting',file=sys.stderr,flush=True)
 async with BleakClient(device) as client:
  print('BLE: connected',file=sys.stderr,flush=True)
  print(json.dumps({'ready':True}),flush=True)
  while True:
   line=await asyncio.to_thread(sys.stdin.readline)
   if not line:return
   request=json.loads(line);op=request['op']
   if op=='close':return
   channel=request['channel']
   if channel not in ['ff51','ff52','ff53']:raise ValueError('Unknown characteristic')
   uuid=SERVICE.replace('ff50',channel)
   if op=='exchange':await client.write_gatt_char(uuid,bytes.fromhex(request['hex']),response=True)
   elif op!='read':raise ValueError('Unknown transport operation')
   data=bytes(await client.read_gatt_char(uuid))
   print(json.dumps({'hex':data.hex()}),flush=True)
try:asyncio.run(main())
except Exception as error:
 print(json.dumps({'error':type(error).__name__}),flush=True);sys.exit(1)
