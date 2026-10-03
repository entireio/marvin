#!/usr/bin/env python3
"""Ensure raw AO evidence cannot silently accept malformed/display data."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("exports", Path(__file__).with_name("compare-ao-exports.py"))
exports = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exports)


class ExportTests(unittest.TestCase):
    def test_signed_half_float_and_rejected_payloads(self):
        header = b"\xabKTX 11\xbb\r\n\x1a\n" + struct.pack(
            "<13I", 0x04030201, 0x140B, 2, 0x1908, 0x881A, 0x1908, 1, 1, 0, 0, 1, 1, 0)
        valid = header + struct.pack("<I4e", 8, .25, -.5, 1, -4)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.ktx"
            path.write_bytes(valid)
            pixels, _ = exports.read_rgba16f(path, (1, 1))
            self.assertEqual(pixels.tolist(), [[[.25, -.5, 1, -4]]])
            with self.assertRaises(ValueError):
                exports.read_rgba16f(path, (2, 1))
            for data in (valid[:-1], valid + b"\0", b"x" + valid[1:],
                         header + struct.pack("<I4e", 8, float("nan"), 0, 0, 0)):
                with self.subTest(data=data):
                    path.write_bytes(data)
                    with self.assertRaises(ValueError):
                        exports.read_rgba16f(path, (1, 1))


if __name__ == "__main__":
    unittest.main()
