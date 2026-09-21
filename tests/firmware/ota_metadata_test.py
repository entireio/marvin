"""Verify CRC against the pinned ESP-IDF ROM implementation, not a duplicate."""
import ctypes
import importlib.util
import struct
import subprocess
import tempfile
from pathlib import Path
root=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('metadata',root/'firmware/tools/ota_metadata.py');metadata=importlib.util.module_from_spec(spec);spec.loader.exec_module(metadata)
with tempfile.TemporaryDirectory(prefix='marvin-ota-format-') as directory:
    library=Path(directory)/'crc.dylib'
    subprocess.run(['cc','-shared','-fPIC','-I',str(root/'work/esp-idf/components/esp_rom/include'),str(root/'work/esp-idf/components/esp_rom/linux/esp_rom_crc.c'),'-o',str(library)],check=True,capture_output=True)
    crc=ctypes.CDLL(str(library)).esp_rom_crc32_le;crc.argtypes=[ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32];crc.restype=ctypes.c_uint32
    for sequence in [1,2,3,4,65535,0xfffffffe]:
        value=metadata.entry(sequence,metadata.VALID)
        assert struct.unpack_from('<I',value,28)[0] == crc(0xffffffff,value[:4],4)
    def sectors(a,b):
        return a+b'\xff'*(4096-len(a))+b+b'\xff'*(4096-len(b))
    previous=metadata.entry(1,metadata.VALID)
    candidate=metadata.entry(2,metadata.NEW)
    assert metadata.inspect(sectors(previous,candidate))['selectedSlot']==1
    assert metadata.inspect(sectors(previous,metadata.entry(2,metadata.ABORTED)))['selectedSlot']==0
    assert metadata.inspect(sectors(previous,metadata.entry(2,metadata.INVALID)))['selectedSlot']==0
    assert metadata.inspect(sectors(previous,metadata.entry(2,metadata.UNDEFINED)))['selectedSlot']==1
    for i in range(4):
        corrupted=bytearray(candidate);corrupted[i]^=1
        assert metadata.inspect(sectors(previous,corrupted))['selectedSlot']==0
    assert metadata.inspect(b'\xff'*8192)['selectedSlot'] is None
    for bad in [0,0xffffffff,-1]:
        try: metadata.entry(bad,metadata.VALID)
        except ValueError: pass
        else: raise AssertionError('Invalid sequence accepted')
print('OTA metadata fixture: generated CRCs match pinned ESP-IDF ROM implementation; slot/state/corruption cases passed. No metadata or flash image written.')
