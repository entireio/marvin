#!/usr/bin/env python3
"""Render a few deterministic capture modes with a real rendering driver and compare them with Godot on macOS.

    tools/ci/render-captures.py --godot GODOT_CONSOLE_EXE --driver d3d12 --out OUT
    tools/ci/render-captures.py --godot GODOT_CONSOLE_EXE --driver vulkan --label "Vulkan (lavapipe)" --out OUT
    tools/ci/render-captures.py --godot "$GODOT" --driver metal --out OUT --update-reference    # on a Mac

Meant for GPU-less CI runners (.github/workflows/godot-windows.yml): Direct3D 12 there can only get Microsoft's WARP
software adapter, Vulkan only a software implementation such as Mesa's lavapipe. The first mode is a probe: when the
driver does not start (Godot then falls back or quits) the script says why from the log and stops. Captures are
compared with tools/ci/reference/godot-mac-captures/MODE/ (the same modes rendered by the editor runtime on a Mac with
Metal, downscaled losslessly to keep them small: see REFERENCE_SCALE) as the mean |sRGB difference| /255, also per
capture. A driver that does not render is not a failure (exit 0) unless --strict; the job summary says what ran and
what did not. A mode that ends with a Windows exception status (a crash, also while quitting after its captures) is
one: exit 1.
Needs Pillow for the image statistics (pip install pillow).
"""
import argparse, json, os, re, shutil, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cilib import REFERENCE, ROOT, append_summary, exit_text, is_crash, md_escape, read_text, write_json

CAPTURES = REFERENCE / "godot-mac-captures"
TOWN_PINS = {"MARVIN_GRID_SLOTS": "0,3,1,2", "MARVIN_TOWN_DAYLIGHT": "0.2125,4.18"}
# name, flag, environment, timeout seconds, captures to compare (None: all PNGs)
MODES = [
    ("facade-test", "--facade-test", {}, 900, None),
    ("hud-smoke-test", "--hud-smoke-test", {}, 900, None),
    ("town-smoke-test", "--town-smoke-test", TOWN_PINS, 1500, ["town-overview.png"]),
]
REFERENCE_SCALE = 2   # references are stored at half size (box filter); captures are reduced the same way to compare


def device_line(log):
    for line in log.splitlines():
        if re.search(r"(Vulkan|D3D12|Metal|OpenGL)[^\n]*(Using Device|Forward\+|Compatibility)", line):
            return line.strip()
    return None


def why_not(log):
    keep = [l.strip() for l in log.splitlines() if re.search(r"ERROR|error|failed|Failed|Unable|not supported|fallback|Falling back|Your video card", l)
            and "Leaked" not in l and "leaked" not in l]
    return keep[:12]


def reduce(image):
    from PIL import Image
    image = image.convert("RGB")
    return image.resize((max(1, image.width // REFERENCE_SCALE), max(1, image.height // REFERENCE_SCALE)), Image.Resampling.BOX)


def mean_difference(a, b):
    from PIL import ImageChops, ImageStat
    if a.size != b.size:
        return None
    stat = ImageStat.Stat(ImageChops.difference(a, b))
    return sum(stat.mean) / len(stat.mean)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--godot", required=True)
    parser.add_argument("--driver", required=True, help="d3d12 | vulkan | metal | opengl3")
    parser.add_argument("--label")
    parser.add_argument("--out", required=True)
    parser.add_argument("--only")
    parser.add_argument("--update-reference", action="store_true")
    parser.add_argument("--strict", action="store_true")
    args = parser.parse_args()
    label = args.label or args.driver
    out = Path(args.out).resolve() / args.driver
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)
    method = "gl_compatibility" if args.driver.startswith("opengl") else "forward_plus"
    cmd = [args.godot, "--path", str(ROOT), "--rendering-driver", args.driver, "--rendering-method", method,
           "--audio-driver", "Dummy", "--"]
    results, md = [], [f"## Rendering without a GPU: {label}", ""]
    rows = []
    started = False
    for name, flag, env, timeout, wanted in MODES:
        if args.only and name not in args.only.split(","):
            continue
        directory = out / name
        directory.mkdir(parents=True)
        log_path = out / f"{name}.log"
        start = time.time()
        with open(log_path, "wb") as f:
            try:
                code = subprocess.run(cmd + [flag, str(directory)], stdout=f, stderr=subprocess.STDOUT,
                                      env=dict(os.environ, **env), timeout=timeout).returncode
                timed_out = False
            except subprocess.TimeoutExpired:
                code, timed_out = None, True
        seconds = round(time.time() - start, 1)
        log = read_text(log_path)
        device = device_line(log)
        pngs = sorted(p.name for p in directory.glob("*.png"))
        entry = {"mode": name, "exit": code, "timedOut": timed_out, "seconds": seconds, "device": device, "captures": len(pngs)}
        for report in sorted(directory.glob("*.json")):
            try:
                verdict = json.loads(read_text(report)).get("passed")
            except (ValueError, AttributeError):
                verdict = None
            if isinstance(verdict, bool):
                entry["passed"] = verdict if entry.get("passed", True) else False
        # Godot falls back to another driver when the requested one does not start (project.godot: fallback_to_vulkan,
        # fallback_to_d3d12): that is not a run of this driver.
        api = {"vulkan": "Vulkan", "d3d12": "D3D12", "metal": "Metal", "opengl3": "OpenGL"}.get(args.driver)
        if api and device and not device.startswith(api):
            entry["fellBack"] = device
            entry["why"] = why_not(log)
            results.append(entry)
            rows.append((name, code, timed_out, seconds, device, len(pngs), None, [], []))
            break
        compared = []
        try:
            from PIL import Image, ImageStat
            blank = 0
            for png in pngs:
                image = Image.open(directory / png).convert("RGB")
                extrema = image.getextrema()
                if all(lo == hi for lo, hi in extrema):
                    blank += 1
                if wanted is not None and png not in wanted:
                    continue
                small = reduce(image)
                ref = CAPTURES / name / png
                if args.update_reference:
                    ref.parent.mkdir(parents=True, exist_ok=True)
                    small.save(ref, optimize=True)
                elif ref.exists():
                    d = mean_difference(small, Image.open(ref).convert("RGB"))
                    compared.append((png, d))
            entry["blank"] = blank
        except ImportError:
            entry["blank"] = None
        entry["compared"] = [{"capture": p, "meanDifference": d} for p, d in compared]
        if not pngs and (code != 0 or timed_out):
            entry["why"] = why_not(log)
        results.append(entry)
        values = [d for _, d in compared if d is not None]
        rows.append((name, code, timed_out, seconds, device, len(pngs), entry.get("blank"), values, compared))
        if not pngs and not started:
            break                     # the driver did not render the probe: stop here
        started = True
    write_json(out / "render.json", results)

    any_rendered = any(r["captures"] and r.get("blank") != r["captures"] and not r.get("fellBack") for r in results)
    first = results[0] if results else {}
    if first.get("device"):
        md.append(f"Device: `{md_escape(first['device'], 200)}`.")
    md.append("")
    md.append(f"Captures are compared at half size (box filter) with the same mode rendered by Godot on macOS (Metal), "
              f"`tools/ci/reference/godot-mac-captures`; window captures need the 1280 x 820 window to fit the screen.")
    md.append("")
    md.append("| Mode | Its check | Exit | Time s | Captures (blank) | Mean /255 vs Godot on macOS (worst) |")
    md.append("|---|---|---|---|---|---|")
    for (name, code, timed_out, seconds, device, count, blank, values, compared), entry in zip(rows, results):
        stats = (f"{sum(values) / len(values):.2f} ({max(values):.2f}) over {len(values)}" if values else "-")
        verdict = {True: "pass", False: "**fail**"}.get(entry.get("passed"), "-")
        md.append(f"| {name} | {verdict} | {exit_text(code, timed_out)} | {seconds} | {count} ({blank if blank is not None else '?'}) | {stats} |")
    if not any_rendered:
        why = first.get("why") or []
        md.append("")
        md.append(f"**{label} did not render**" + (f" (Godot fell back to `{md_escape(first['fellBack'], 120)}`)" if first.get("fellBack") else "")
                  + ". From the log of the first mode:")
        md.append("")
        md.append("```")
        md.extend(why or [l for l in read_text(out / f'{results[0]["mode"]}.log').splitlines()[-15:]] if results else ["(no run)"])
        md.append("```")
    details = []
    for name, code, timed_out, seconds, device, count, blank, values, compared in rows:
        if compared:
            details.append(f"\n<details><summary>{name}: per capture</summary>\n")
            details.append("| Capture | Mean /255 |")
            details.append("|---|---|")
            details += [f"| {p} | {d:.2f} |" if d is not None else f"| {p} | size differs |" for p, d in compared]
            details.append("\n</details>")
    crashes = [r for r in results if is_crash(r["exit"])]
    if crashes:
        md.append("")
        md.append("**FAIL**: " + ", ".join(f"{r['mode']} ended with {exit_text(r['exit'])}" for r in crashes)
                  + (" after its captures (a crash while quitting)" if all(r["captures"] for r in crashes) else "") + ".")
    md.extend(details)
    append_summary("\n".join(md) + "\n")
    return 1 if crashes or (args.strict and not any_rendered) else 0


if __name__ == "__main__":
    sys.exit(main())
