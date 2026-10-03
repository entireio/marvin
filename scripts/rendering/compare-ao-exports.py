#!/usr/bin/env python3
"""Compare Xcode KTX mip-zero exports from original/restored AO passes.

Export as KTX with Adjust Color disabled. This checks the AO input and output,
not final-color equality or performance. Requires NumPy.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

import numpy as np


def read_rgba16f(path, size):
    data = path.read_bytes()
    if data[:12] != b"\xabKTX 11\xbb\r\n\x1a\n" or len(data) < 68:
        raise ValueError(f"{path}: expected KTX1")
    h = struct.unpack_from("<13I", data, 12)
    expected = (0x04030201, 0x140B, 2, 0x1908, 0x881A, 0x1908)
    if h[:6] != expected or h[6:9] != (*size, 0) or h[9] not in (0, 1) or h[10:12] != (1, 1):
        raise ValueError(f"{path}: expected one RGBA16Float 2D mip, dimensions {size}")
    offset = 64 + h[12]
    length = size[0] * size[1] * 8
    if len(data) != offset + 4 + length or struct.unpack_from("<I", data, offset)[0] != length:
        raise ValueError(f"{path}: invalid payload length")
    pixels = np.frombuffer(data, dtype="<f2", offset=offset + 4).reshape(size[1], size[0], 4).astype(np.float32)
    if not np.isfinite(pixels).all():
        raise ValueError(f"{path}: nonfinite pixels")
    return pixels, hashlib.sha256(data).hexdigest()


def compare(directory):
    result = {"scope": "AO mip-zero input and output only; not main shading, gameplay, or timing acceptance", "exports": {}}
    for kind, size in (("input", (1920, 1080)), ("output", (960, 540))):
        before, before_hash = read_rgba16f(directory / f"ao-{kind}-before.ktx", size)
        after, after_hash = read_rgba16f(directory / f"ao-{kind}-after.ktx", size)
        delta = np.abs(before - after)
        result["exports"][kind] = {
            "dimensions": list(size), "beforeSHA256": before_hash, "afterSHA256": after_hash,
            "changedPixels": int(np.any(delta != 0, axis=2).sum()),
            "changedPixelsPerChannel": np.count_nonzero(delta, axis=(0, 1)).tolist(),
            "maximumDifferencePerChannel": delta.max(axis=(0, 1)).tolist(),
        }
    result["aoBuffersEqual"] = all(row["changedPixels"] == 0 for row in result["exports"].values())
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = compare(args.directory)
    text = json.dumps(result, indent=2) + "\n"
    (args.directory / "raw-ao-comparison.json").write_text(text)
    print(text, end="")
