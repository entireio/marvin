#!/usr/bin/env python3
"""Compare SimulationChecks outputs (tools/checks) with the Swift reference and report the differences.

    tools/ci/checks-report.py --reference src/Checks/reference/simulation-checks-swift.txt \
        --run "default|out/checks-default.txt|0" --run "MARVIN_PORTABLE_MATH=1|out/checks-portable.txt|0" --out out/checks

Each --run is LABEL|FILE|EXIT_CODE. Line endings are normalised. A numeric difference is a finding, not a failure:
the report lists every differing line with the numbers that differ and their relative difference. The script exits 1
only when a run crashed or failed a check (non-zero exit code) or its output is missing. Writes OUT/checks.json and
OUT/checks.diff and appends a Markdown section to the job summary.
"""
import argparse, difflib, re, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cilib import append_summary, md_escape, read_text, relative_difference, write_json

NUMBER = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


def numeric_differences(a, b):
    """Pairs of differing numbers when two lines differ only in their numbers, else None."""
    if NUMBER.sub("#", a) != NUMBER.sub("#", b):
        return None
    pairs = [(x, y) for x, y in zip(NUMBER.findall(a), NUMBER.findall(b)) if x != y]
    return [(x, y, relative_difference(x, y)) for x, y in pairs]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", required=True)
    parser.add_argument("--run", action="append", required=True, help="LABEL|FILE|EXIT_CODE")
    parser.add_argument("--out", required=True)
    parser.add_argument("--title", default="Core maths: SimulationChecks against the Swift reference")
    args = parser.parse_args()

    reference = read_text(args.reference).splitlines()
    results, failed, md = [], False, [f"## {args.title}", ""]
    md.append(f"Reference: `{Path(args.reference).as_posix()}` ({len(reference)} lines, Swift debug build on macOS).")
    md.append("")
    md.append("| Run | Exit | Lines | Result |")
    md.append("|---|---|---|---|")
    diffs, details = [], []
    for spec in args.run:
        label, path, code = (spec.split("|") + ["", ""])[:3]
        exit_code = int(code) if code.strip() else 0
        if not Path(path).exists():
            results.append({"run": label, "exit": exit_code, "missing": True}); failed = True
            md.append(f"| {label} | {exit_code} | - | **FAIL**: no output |"); continue
        lines = read_text(path).splitlines()
        matcher = difflib.SequenceMatcher(a=reference, b=lines, autojunk=False)
        changed = []
        for op, i1, i2, j1, j2 in matcher.get_opcodes():
            if op == "equal": continue
            ref_part, act_part = reference[i1:i2], lines[j1:j2]
            for k in range(max(len(ref_part), len(act_part))):
                a = ref_part[k] if k < len(ref_part) else None
                b = act_part[k] if k < len(act_part) else None
                nums = numeric_differences(a, b) if a is not None and b is not None else None
                changed.append({"reference_line": i1 + k + 1 if a is not None else None, "reference": a, "actual": b,
                                "numbers": [{"reference": x, "actual": y, "relative": str(r) if r is not None else None} for x, y, r in nums] if nums else None,
                                "numeric_only": nums is not None})
        identical = not changed
        crashed = exit_code != 0
        failed = failed or crashed
        if crashed:
            verdict = f"**FAIL**: exit code {exit_code} (crash or failed check)"
        elif identical:
            verdict = "identical to the Swift reference"
        else:
            numeric = sum(1 for c in changed if c["numeric_only"])
            verdict = f"**FINDING**: {len(changed)} line(s) differ ({numeric} only in their numbers)"
        results.append({"run": label, "exit": exit_code, "lines": len(lines), "identical": identical, "differences": changed})
        md.append(f"| {md_escape(label)} | {exit_code} | {len(lines)} | {verdict} |")
        if changed:
            diffs.append("".join(difflib.unified_diff([l + "\n" for l in reference], [l + "\n" for l in lines],
                                                      "swift-reference", label, n=0)))
            details.append(f"\n<details open><summary>{md_escape(label)}: differing lines</summary>\n")
            details.append("| Ref line | Swift reference | This runner | Numbers (reference → this runner, relative difference) |")
            details.append("|---|---|---|---|")
            for c in changed[:60]:
                nums = "; ".join(f"{n['reference']} → {n['actual']} ({float(n['relative']):.2e})" if n["relative"] else f"{n['reference']} → {n['actual']}"
                                 for n in (c["numbers"] or []))
                details.append(f"| {c['reference_line'] or '-'} | `{md_escape(c['reference'] or '(none)')}` | `{md_escape(c['actual'] or '(none)')}` | {nums or '-'} |")
            if len(changed) > 60:
                details.append(f"| | ... {len(changed) - 60} more in checks.diff | | |")
            details.append("\n</details>")
    # Do the runs agree with each other (the portable hypot is the default off macOS, so they should)?
    texts = [read_text(spec.split("|")[1]) for spec in args.run if Path(spec.split("|")[1]).exists()]
    if len(texts) > 1:
        same = all(t == texts[0] for t in texts[1:])
        md.append("")
        md.append(f"The runs are {'byte-identical to each other' if same else '**different from each other**'}.")
    md.extend(details)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    write_json(out / "checks.json", results)
    (out / "checks.diff").write_text("\n".join(diffs), encoding="utf-8")
    append_summary("\n".join(md) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
