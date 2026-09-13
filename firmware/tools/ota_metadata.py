"""Pinned ESP-IDF5.4 OTA selection encoding for offline planning/tests.

No command-line writer or serial access: callers must independently establish
image validity. Marking an image VALID is a health decision, not a CRC check.
"""
import struct
import zlib

NEW, PENDING_VERIFY, VALID, INVALID, ABORTED, UNDEFINED = 0, 1, 2, 3, 4, 0xffffffff

def entry(sequence: int, state: int) -> bytes:
    if not 1 <= sequence < 0xffffffff or state not in (NEW, PENDING_VERIFY, VALID, INVALID, ABORTED, UNDEFINED):
        raise ValueError('Invalid OTA selection entry')
    seq = struct.pack('<I', sequence)
    return seq + bytes(20) + struct.pack('<II', state, zlib.crc32(seq, 0xffffffff))

def inspect(data: bytes):
    if len(data) != 8192:
        raise ValueError('OTA metadata must contain two full sectors')
    records = []
    for offset in (0, 4096):
        sequence, = struct.unpack_from('<I', data, offset)
        state, crc = struct.unpack_from('<II', data, offset+24)
        valid = sequence != 0xffffffff and state not in (INVALID, ABORTED) and crc == zlib.crc32(data[offset:offset+4], 0xffffffff)
        records.append({'sequence': sequence, 'state': state, 'crcValidAndSelectable': valid, 'slot': (sequence-1)%2 if valid else None})
    selectable = [(r['sequence'], i) for i, r in enumerate(records) if r['crcValidAndSelectable']]
    # ESP-IDF picks the first record on equal sequences.
    selected = max(selectable, key=lambda item:(item[0],-item[1]))[1] if selectable else None
    return {'records': records, 'selectedRecord': selected, 'selectedSlot': records[selected]['slot'] if selected is not None else None}
