#!/usr/bin/env python3
"""Compare the libm probes of several platforms (tools/checks --libm-probe DIR) with macOS.

    tools/ci/libm-report.py --reference "macOS|probes/macos" --run "Windows|probes/windows" --run "Linux|probes/linux" --out OUT
    tools/ci/libm-report.py --reference "macOS|DIR" --update-reference     # on a Mac: store DIR's hashes

Every probe file holds, per call, the argument bits and the result bits. For each function and platform the report
counts the calls whose result differs from the reference platform's (same arguments), the largest difference in units
in the last place and an example. tools/ci/reference/libm-mac-sha256.json holds the hashes of a probe run on the
Mac that made the SimulationChecks reference, so the macOS runner can be checked against that Mac as well.
Appends a Markdown section to the job summary and writes OUT/libm.json. Never fails: differences are findings.
"""
import argparse, hashlib, json, struct, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cilib import REFERENCE, append_summary, write_json

MAC_HASHES = REFERENCE / "libm-mac-sha256.json"
ARITY = {"atan2": 2, "pow": 2, "hypot": 2, "atan2f": 2, "powf": 2}


SINGLE = {"sinf", "cosf", "tanf", "acosf", "atanf", "atan2f", "expf", "logf", "powf", "sqrtf", "simd_rsqrtf", "simd_recipf"}


def calls(path):
    """[(argument bytes, arguments, result bits as a signed integer)], and whether the function is single precision."""
    single = path.stem in SINGLE
    width, fmt, ifmt = (4, "<f", "<i") if single else (8, "<d", "<q")
    arity = ARITY.get(path.stem, 1)
    data = path.read_bytes()
    step = width * (arity + 1)
    out = []
    for i in range(0, len(data) - step + 1, step):
        chunk = data[i:i + step]
        args = tuple(struct.unpack(fmt, chunk[k * width:(k + 1) * width])[0] for k in range(arity))
        out.append((chunk[:arity * width], args, struct.unpack(ifmt, chunk[arity * width:])[0]))
    return out, single


def ulps(a, b, single):
    """Units in the last place between two results given as signed bit patterns (ordered across the sign)."""
    offset = 1 << (31 if single else 63)
    key = lambda x: x if x >= 0 else -(x + offset)
    return abs(key(a) - key(b))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True, help="LABEL|DIR")
    parser.add_argument("--run", action="append", default=[], help="LABEL|DIR")
    parser.add_argument("--out")
    parser.add_argument("--update-reference", action="store_true")
    args = parser.parse_args()
    ref_label, ref_dir = args.reference.split("|", 1)
    ref_dir = Path(ref_dir)
    files = sorted(ref_dir.glob("*.bin"))
    if args.update_reference:
        write_json(MAC_HASHES, {f.stem: hashlib.sha256(f.read_bytes()).hexdigest() for f in files})
        print(f"wrote {len(files)} hashes to {MAC_HASHES}")
        return 0

    md = ["## Which libm functions differ from macOS", "",
          f"`tools/checks --libm-probe`: the C library functions the simulation calls through .NET's Math/MathF, "
          f"65,536 calls each in the game's argument ranges, compared call by call with {ref_label}. "
          "sqrt is a control; hypot, simd_rsqrtf and simd_recipf are the port's own reproductions of Darwin's hypot and "
          "the NEON estimate instructions, so they must match.", ""]
    if MAC_HASHES.exists():
        mac = json.loads(MAC_HASHES.read_text())
        same = [f.stem for f in files if mac.get(f.stem) == hashlib.sha256(f.read_bytes()).hexdigest()]
        md.append(f"{ref_label} runner against the Mac that made the Swift reference (macOS 27, M2): "
                  + ("**identical** probe results for all functions." if len(same) == len(mac) == len(files)
                     else f"{len(same)} of {len(mac)} functions identical; different: "
                     + ", ".join(sorted(set(mac) - set(same))) + "."))
        md.append("")
    labels = [r.split("|", 1)[0] for r in args.run]
    md.append("| Function | " + " | ".join(f"{l}: calls that differ (max ulps)" for l in labels) + " |")
    md.append("|---|" + "---|" * len(labels))
    result, examples = {}, []
    for f in files:
        reference, single = calls(f)
        row = [f.stem]
        for spec in args.run:
            label, directory = spec.split("|", 1)
            path = Path(directory) / f.name
            if not path.exists():
                row.append("missing"); continue
            here, _ = calls(path)
            mismatched_args = sum(1 for a, b in zip(reference, here) if a[0] != b[0])
            differ = [(a, b) for a, b in zip(reference, here) if a[0] == b[0] and a[2] != b[2]]
            worst = max((ulps(a[2], b[2], single) for a, b in differ), default=0)
            result.setdefault(f.stem, {})[label] = {"calls": len(here), "differ": len(differ), "maxUlps": worst,
                                                     "argumentMismatches": mismatched_args}
            text = "0" if not differ else f"**{len(differ)}** ({100 * len(differ) / len(here):.2f} %, {worst} ulp{'s' if worst > 1 else ''})"
            if mismatched_args:
                text += f"; {mismatched_args} argument mismatches"
            row.append(text)
            if differ:
                a, b = differ[0]
                conv = (lambda bits: struct.unpack("<f", struct.pack("<i", bits))[0]) if single else (lambda bits: struct.unpack("<d", struct.pack("<q", bits))[0])
                examples.append(f"| {f.stem} | {label} | {', '.join(repr(x) for x in a[1])} | {conv(a[2])!r} | {conv(b[2])!r} |")
        md.append("| " + " | ".join(row) + " |")
    if examples:
        md += ["", "<details><summary>First differing call per function and platform</summary>", "",
               f"| Function | Platform | Arguments | {ref_label} | Platform |", "|---|---|---|---|---|"] + examples + ["", "</details>"]
    if args.out:
        write_json(Path(args.out) / "libm.json", result)
    append_summary("\n".join(md) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
