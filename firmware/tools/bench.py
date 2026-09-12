"""Encrypted bench scan/connect client using Espressif Security 2, never backend enrollment."""
import argparse, asyncio, getpass, json, os, sys
from pathlib import Path
sys.path.insert(0, str(Path(os.environ['IDF_PATH']) / 'tools/esp_prov'))
from security.security2 import Security2
from bleak import BleakClient, BleakScanner
p = argparse.ArgumentParser()
p.add_argument('--credentials', type=Path, required=True)
p.add_argument('--apply', action='store_true', help='Interactively choose and apply a robot-scanned network')
a = p.parse_args()
SERVICE = '0000ff50-0000-1000-8000-00805f9b34fb'
SESSION = SERVICE.replace('ff50', 'ff52')
CONTROL = SERVICE.replace('ff50', 'ff53')
async def main():
    credentials = json.loads(a.credentials.read_text())
    device = await BleakScanner.find_device_by_filter(lambda d, ad: SERVICE in ad.service_uuids, timeout=20)
    if not device:
        raise RuntimeError('No Marvin in its five-minute setup window')
    async with BleakClient(device) as client:
        async def exchange(endpoint, data):
            await client.write_gatt_char(endpoint, data, response=True)
            return bytes(await client.read_gatt_char(endpoint))
        secure = Security2(1, credentials['username'], credentials['password'], False)
        response = None
        while True:
            message = secure.security_session(response)
            if message is None:
                break
            response = await exchange(SESSION, message)
        async def command(value):
            return json.loads(secure.decrypt_data(await exchange(CONTROL, secure.encrypt_data(json.dumps(value).encode()))))
        async def wait():
            for _ in range(100):
                status = await command({'op': 'status'})
                if status['phase'] not in ['scanning', 'connecting', 'checking_backend', 'restoring_previous']:
                    return status
                await asyncio.sleep(1)
            raise TimeoutError('Robot did not complete operation')
        await command({'op': 'scan'})
        status = await wait()
        print(json.dumps(status))
        for i in range(status['count']):
            print(i, json.dumps(await command({'op': 'network', 'index': i})))
        if a.apply:
            index = int(input('Network index: '))
            password = getpass.getpass('Wi-Fi password (empty for open): ')
            response = await command({'op': 'apply', 'index': index, 'scanGeneration': status['scanGeneration'], 'password': password})
            del password
            if 'error' in response:
                raise RuntimeError(response['error'])
            print(json.dumps(await wait()))
asyncio.run(main())
