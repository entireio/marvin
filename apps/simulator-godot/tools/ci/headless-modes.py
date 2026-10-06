#!/usr/bin/env python3
"""Run the game's deterministic logic modes headless and compare their reports with committed references.

    tools/ci/headless-modes.py --godot GODOT_CONSOLE_EXE --out OUT            # editor runtime (project in this folder)
    tools/ci/headless-modes.py --exe build/windows/MarvinSimulator.console.exe --out OUT --only town-statistics
    tools/ci/headless-modes.py --godot "$GODOT" --out OUT --update-reference  # on a Mac: refresh tools/ci/reference/godot-mac

Headless (Godot's dummy renderer) nothing is drawn: the SceneKit facade hands out blank snapshots
(SceneKitRuntime.ViewportImage), so the capture modes still run their logic and write their reports; keys computed
from pixels then describe blank images, the same on every platform. Every report is compared key by key with
  - tools/ci/reference/godot-mac/MODE/: the same headless run of the editor runtime on macOS (the expected result:
    any difference is a platform difference), large text dumps as digests (cilib.digest), and runs.json (exit codes);
  - tools/ci/reference/macos/AREA/: the macOS game's own report where one exists (informational: headless, pixel
    counts differ from a rendered run).
Pins as in PORTING.md ("Validation", the reference runs). A difference is a finding, not a failure. Exits 1 only when
a mode that exits 0 on macOS crashed, timed out without writing its reports or exited non-zero.
Writes OUT/MODE/ (reports and log), OUT/headless-modes.json and a Markdown section of the job summary.
"""
import argparse, hashlib, json, os, shutil, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cilib import (REFERENCE, ROOT, append_summary, compare_digest, compare_json, digest, exit_text, is_crash, load_json,
                   md_escape, read_text, show, write_json)

SMOKE_PINS = {"MARVIN_GRID_SLOTS": "0,3,1,2;0,3,1,2;3,1,2,0", "MARVIN_DAYLIGHT_FRACTION": "0.515,0.795,0.8305",
              "MARVIN_DAYLIGHT_PHASE": "2.75,2.065,2.5753"}
TOWN_PINS = {"MARVIN_GRID_SLOTS": "0,3,1,2", "MARVIN_TOWN_DAYLIGHT": "0.2125,4.18"}
MACOS = REFERENCE / "macos"
GODOT_MAC = REFERENCE / "godot-mac"

# name, flag, environment, reports, macOS reference folder (tools/ci/reference/macos/...), timeout in seconds
MODES = [
    ("town-statistics", "--town-statistics", {"MARVIN_MAC_REFERENCE": str(MACOS / "town")}, ["town-statistics.json"], "town", 600),
    ("town-reference-dump", "--town-reference-dump", {}, ["town-reference.txt"], None, 600),
    ("town-people-dump", "--town-people-dump", {}, ["town-people.txt"], None, 600),
    ("audio-facade-test", "--audio-facade-test", {}, ["audio-facade.json"], None, 600),
    ("audio-smoke-test", "--audio-smoke-test", {}, ["audio.json"], None, 900),
    ("storm-race-test", "--storm-race-test", {}, ["storm-grids.json"], None, 1200),
    ("smoke-test", "--smoke-test", SMOKE_PINS, ["smoke.json", "full-race-trails.json"], "smoke", 1200),
    ("town-smoke-test", "--town-smoke-test", TOWN_PINS, ["town-smoke.json"], "town", 900),
    ("entrance-smoke-test", "--entrance-smoke-test", TOWN_PINS, ["entrances.json", "activities.json", "pedestrian-access.json"], "entrance", 900),
    ("people-smoke-test", "--people-smoke-test", TOWN_PINS, ["people.json", "street-people.json"], "people", 900),
    ("character-smoke-test", "--character-smoke-test", {}, ["characters.json"], "character", 900),
    ("binary-sky-smoke-test", "--binary-sky-smoke-test", {"MARVIN_DAYLIGHT_REFERENCE": str(MACOS / "binary-sky" / "binary-races.json")}, ["binary-races.json"], "binary-sky", 900),
]
# Binary outputs compared by SHA-256 (written as a JSON report, FILE-sha256.json, next to the mode's reports).
HASHED = {"audio-smoke-test": ("*.wav", "wav-sha256.json")}
# Keys that name the machine, not the result.
IGNORE = [r"reference", r"directory", r"outputDirectory", r"captureDirectory"]


def run_mode(cmd, out, name, flag, env, timeout):
    directory = out / name
    shutil.rmtree(directory, ignore_errors=True)
    directory.mkdir(parents=True)
    log = out / f"{name}.log"
    environment = dict(os.environ, **env)
    start = time.time()
    with open(log, "wb") as f:
        try:
            proc = subprocess.run(cmd + [flag, str(directory)], stdout=f, stderr=subprocess.STDOUT, env=environment, timeout=timeout)
            code, timed_out = proc.returncode, False
        except subprocess.TimeoutExpired:
            code, timed_out = None, True
    return code, timed_out, round(time.time() - start, 1), log


def log_tail(log, lines=25):
    text = read_text(log).splitlines()
    keep = [l for l in text if "Leaked instance" not in l and "utilities.cpp" not in l and "leaked at exit" not in l]
    return "\n".join(keep[-lines:])


def town_identity(out, results):
    """The town's identity first: --town-statistics against the macOS game (the mode compares every key itself,
    MARVIN_MAC_REFERENCE) and --town-reference-dump against Godot on macOS (every street, lot, building, entrance,
    route, collision body and scene node)."""
    lines = []
    path = out / "town-statistics" / "town-statistics.json"
    if path.exists():
        data = json.loads(read_text(path))
        comparison = data.get("comparison", {})
        differing = [k for k, v in comparison.items() if not v.get("match")]
        if "identical" not in data:
            lines.append("**Town identity** (`--town-statistics`): no macOS reference was found by the mode.")
        elif data["identical"]:
            lines.append(f"**Town identity** (`--town-statistics` against the macOS game's town-smoke.json): "
                         f"**identical**, all {len(comparison)} keys (buildings {data['town'].get('buildings')}, "
                         f"accessible compounds {data['town'].get('accessibleCompounds')}, people {data['town'].get('people')}).")
        else:
            lines.append(f"**Town identity** (`--town-statistics` against the macOS game's town-smoke.json): "
                         f"**DIFFERS** in {len(differing)} of {len(comparison)} keys: "
                         + ", ".join(f"`{k}` macOS {comparison[k].get('mac')} here {comparison[k].get('godot')}" for k in differing))
    dump = next((r for r in results if r["mode"] == "town-reference-dump"), None)
    if dump and dump["reports"] and "godotMac" in dump["reports"][0]:
        g = dump["reports"][0]["godotMac"]
        if g["identical"]:
            lines.append("**Town layout** (`--town-reference-dump`: streets, lots, buildings, entrances, "
                         "routes, collision bodies, scene nodes): **byte-identical** to Godot on macOS.")
        else:
            lines.append("**Town layout** (`--town-reference-dump`): **DIFFERS** from Godot on macOS: "
                         + ", ".join(f"{k['kind']} {k['differingCount']} of {k['referenceCount']}" for k in g["kinds"]) + " records.")
    return [l + "\n" for l in lines] if lines else []


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--godot", help="the Godot .NET editor binary (console variant on Windows)")
    group.add_argument("--exe", help="an exported game (MarvinSimulator.console.exe)")
    parser.add_argument("--out", required=True)
    parser.add_argument("--only", help="comma-separated mode names")
    parser.add_argument("--engine-args", help="more Godot arguments, e.g. --verbose")
    parser.add_argument("--title", default="Headless game modes against Godot on macOS and the macOS game")
    parser.add_argument("--update-reference", action="store_true", help="store this run as tools/ci/reference/godot-mac")
    parser.add_argument("--mark-unstable", action="store_true",
                        help="on a Mac, after --update-reference: record the keys that differ in this second run as varying "
                             "between runs (tools/ci/reference/godot-mac/unstable.json); they are listed, not counted")
    args = parser.parse_args()

    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    extra = args.engine_args.split() if args.engine_args else []
    cmd = ([args.godot, "--headless", "--audio-driver", "Dummy", *extra, "--path", str(ROOT), "--"] if args.godot
           else [str(Path(args.exe).resolve()), "--headless", "--audio-driver", "Dummy", *extra])
    selected = [m for m in MODES if not args.only or m[0] in args.only.split(",")]
    unstable_path = GODOT_MAC / "unstable.json"
    unstable = json.loads(unstable_path.read_text()) if unstable_path.exists() and not args.update_reference else {}
    runs_path = GODOT_MAC / "runs.json"
    mac_runs = json.loads(runs_path.read_text()) if runs_path.exists() else {}

    results, failed = [], False
    rows, details = [], []
    for name, flag, env, reports, macos_area, timeout in selected:
        print(f"== {name}", flush=True)
        code, timed_out, seconds, log = run_mode(cmd, out, name, flag, env, timeout)
        mac = mac_runs.get(name, {})
        if name in HASHED:
            pattern, report_name = HASHED[name]
            files = sorted((out / name).glob(pattern))
            if files:
                write_json(out / name / report_name, {f.name: hashlib.sha256(f.read_bytes()).hexdigest() for f in files})
            reports = reports + [report_name]
        entry = {"mode": name, "exit": code, "timedOut": timed_out, "seconds": seconds, "macExit": mac.get("exit"),
                 "macSeconds": mac.get("seconds"), "reports": []}
        missing_reports = 0
        for report in reports:
            path = out / name / report
            item = {"file": report}
            if not path.exists():
                item["missing"] = True; missing_reports += 1
                entry["reports"].append(item)
                rows.append((name, code, timed_out, seconds, mac, report, "**missing**", "-")); continue
            if args.update_reference:
                target = GODOT_MAC / name
                target.mkdir(parents=True, exist_ok=True)
                if report.endswith(".txt"):
                    write_json(target / (report + ".digest.json"), digest(read_text(path)))
                else:
                    shutil.copyfile(path, target / report)
            # Against Godot on macOS.
            if report.endswith(".txt"):
                ref = GODOT_MAC / name / (report + ".digest.json")
                if ref.exists():
                    identical, kinds = compare_digest(json.loads(ref.read_text()), read_text(path))
                    item["godotMac"] = {"identical": identical, "kinds": [{"kind": k, "referenceCount": rc, "count": c, "differingCount": len(d), "differing": d[:200], "examples": e} for k, rc, c, d, e in kinds]}
                    vs_godot = "identical" if identical else "**FINDING**: " + ", ".join(f"{k} {len(d)} of {rc}" for k, rc, c, d, e in kinds) + " records differ"
                    if not identical:
                        details.append(f"\n<details open><summary>{name} / {report}: records that differ from Godot on macOS</summary>\n")
                        details.append("| Kind | Records (Mac / here) | Differing records | First differing records here |")
                        details.append("|---|---|---|---|")
                        for k, rc, c, d, e in kinds:
                            details.append(f"| {k} | {rc} / {c} | {len(d)}: {', '.join(map(str, d[:12]))}{' ...' if len(d) > 12 else ''} | {'<br>'.join('`' + md_escape(x, 200) + '`' for x in e)} |")
                        details.append("\n</details>")
                else:
                    vs_godot = "no reference"
            else:
                ref = GODOT_MAC / name / report
                if ref.exists():
                    same, diffs = compare_json(load_json(ref), load_json(path), IGNORE)
                    known = set(unstable.get(f"{name}/{report}", []))
                    if args.mark_unstable and diffs:
                        unstable[f"{name}/{report}"] = sorted(known | {k for k, *_ in diffs})
                    varying = [d for d in diffs if d[0] in known]
                    diffs = [d for d in diffs if d[0] not in known]
                    item["godotMac"] = {"same": same, "varyOnMac": [k for k, *_ in varying], "differences": [{"key": k, "mac": show(a), "here": show(b), "relative": str(r) if r is not None else None} for k, a, b, r in diffs]}
                    note = f"; {len(known)} key(s) that vary between runs on macOS not counted" if known else ""
                    vs_godot = (f"identical ({same} keys{note})" if not diffs else
                                f"**FINDING**: {len(diffs)} of {same + len(diffs) + len(varying)} keys differ{note}")
                    if diffs:
                        details.append(f"\n<details open><summary>{name} / {report}: keys that differ from Godot on macOS</summary>\n")
                        details.append("| Key | Godot on macOS | Here | Relative difference |")
                        details.append("|---|---|---|---|")
                        for k, a, b, r in diffs[:40]:
                            details.append(f"| `{md_escape(k)}` | `{md_escape(show(a))}` | `{md_escape(show(b))}` | {f'{float(r):.2e}' if r is not None else '-'} |")
                        if len(diffs) > 40:
                            details.append(f"| ... {len(diffs) - 40} more in headless-modes.json | | | |")
                        details.append("\n</details>")
                else:
                    vs_godot = "no reference"
            # Against the macOS game's own report.
            vs_macos = "-"
            if macos_area and not report.endswith(".txt"):
                ref = MACOS / macos_area / report
                if ref.exists():
                    same, diffs = compare_json(load_json(ref), load_json(path), IGNORE)
                    mac_ref = GODOT_MAC / name / report
                    mac_diffs = set()
                    if mac_ref.exists():
                        mac_diffs = {k for k, *_ in compare_json(load_json(ref), load_json(mac_ref), IGNORE)[1]}
                    new = [d for d in diffs if d[0] not in mac_diffs]
                    item["macos"] = {"same": same, "differences": [{"key": k, "macos": show(a), "here": show(b), "alsoOnMac": k in mac_diffs} for k, a, b, r in diffs]}
                    vs_macos = (f"identical ({same} keys)" if not diffs else
                                f"{same} of {same + len(diffs)} keys identical; {len(diffs) - len(new)} differ as on Mac (headless, pins)"
                                + (f", **{len(new)} more here**" if new else ""))
            entry["reports"].append(item)
            rows.append((name, code, timed_out, seconds, mac, report, vs_godot, vs_macos))
        # Failure: a mode that passes on macOS crashed, hung before writing, or failed here.
        expected = code if args.update_reference else mac.get("exit", 0)
        if timed_out and missing_reports:
            entry["failure"] = "timed out without its reports"
        elif is_crash(code) and not missing_reports and not args.update_reference:
            # Every report written, then a Windows exception status: the mode finished and the process crashed while
            # quitting. A finding (listed below), not a failure of the mode.
            entry["crashAtExit"] = exit_text(code)
        elif not timed_out and code != expected:
            entry["failure"] = f"exit code {code}, {expected} on macOS"
        elif missing_reports and not mac.get("missing"):
            entry["failure"] = f"{missing_reports} report(s) missing"
        if entry.get("failure"):
            failed = True
            details.append(f"\n<details open><summary>{name}: {entry['failure']} (log tail)</summary>\n\n```\n{log_tail(log)}\n```\n</details>")
        results.append(entry)
        if args.update_reference:
            mac_runs[name] = {"exit": code, "seconds": seconds}

    if args.update_reference:
        GODOT_MAC.mkdir(parents=True, exist_ok=True)
        write_json(runs_path, mac_runs)
        unstable_path.unlink(missing_ok=True)
    if args.mark_unstable:
        write_json(unstable_path, unstable)
    write_json(out / "headless-modes.json", results)

    md = [f"## {args.title}", ""]
    md += town_identity(out, results)
    md += [f"Runner: `{' '.join(Path(c).name if i == 0 else c for i, c in enumerate(cmd))}`. "
          "Headless: snapshots are blank, so pixel-derived keys describe blank images (as in the Godot-on-macOS reference, "
          "which is the same headless run on a Mac).", "",
          "| Mode | Exit (on Mac) | Time s (on Mac) | Report | vs Godot on macOS (headless) | vs macOS game report |",
          "|---|---|---|---|---|---|"]
    for name, code, timed_out, seconds, mac, report, vs_godot, vs_macos in rows:
        md.append(f"| {name} | {exit_text(code, timed_out)} ({mac.get('exit', '-')}) | {seconds} ({mac.get('seconds', '-')}) | {report} | {vs_godot} | {vs_macos} |")
    failures = [r for r in results if r.get("failure")]
    crashes = [r for r in results if r.get("crashAtExit")]
    md.append("")
    md.append("All modes ran as on macOS." if not failures and not crashes else
              "All modes ran and wrote their reports." if not failures else
              "**FAIL**: " + "; ".join(f"{r['mode']}: {r['failure']}" for r in failures))
    if crashes:
        md.append("")
        md.append(f"**FINDING: crash at exit.** {len(crashes)} of {len(results)} modes wrote every report and then ended with a "
                  "Windows exception status while quitting: " + ", ".join(f"{r['mode']} {r['crashAtExit']}" for r in crashes) + ".")
    md.extend(details)
    append_summary("\n".join(md) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
