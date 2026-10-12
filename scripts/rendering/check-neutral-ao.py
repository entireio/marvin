#!/usr/bin/env python3
"""Compare frozen native neutral AO images, including restoration controls.

Run with a Python environment containing Pillow and NumPy. This intentionally
requires exact equality; a failed control is evidence to investigate, not an
automatic tolerance adjustment. This does not evaluate enabled AO quality.
"""
import argparse
import json
from pathlib import Path
import sys

import numpy as np
from PIL import Image


def check(directory):
    report = json.loads((directory / "loaded-gpu-probe.json").read_text())
    groups = {}
    for row in report["variants"]:
        groups.setdefault(row["variant"], []).append(row)
    assert len(groups.get("production", [])) >= 2
    assert len(groups.get("no-ssao", [])) >= 2
    candidates = [name for name in ("literal-neutral-ao", "neutral-custom-ao") if name in groups]
    assert candidates, "No neutral candidate was rendered"
    comparisons = []

    def compare(a, b, label):
        image_a = np.asarray(Image.open(directory / f"gpu-probe-block-{a['block']}.png")).astype(np.int16)
        image_b = np.asarray(Image.open(directory / f"gpu-probe-block-{b['block']}.png")).astype(np.int16)
        assert image_a.shape == image_b.shape and image_a.shape[:2] == (1080, 1920)
        delta = np.abs(image_a - image_b)
        comparisons.append({"label": label, "blocks": [a["block"], b["block"]],
                            "changedPixels": int(np.any(delta, axis=2).sum()),
                            "maximumChannelError": int(delta.max())})

    for name, rows in groups.items():
        assert len(rows) >= 2, f"No repeat control for {name}"
        for row in rows[1:]:
            compare(rows[0], row, name + " repeat/restoration")
    for name in candidates:
        for row in groups[name]:
            assert row["boundAOMaterialCount"] > 0, "Candidate never bound materials"
            compare(groups["no-ssao"][0], row, name + " versus AO disabled")
    passed = all(row["changedPixels"] == 0 for row in comparisons)
    return {"neutralBindingPassed": passed, "gameplayAccepted": False,
            "scope": "Frozen final color and repeat/restoration controls; no enabled AO or sustained timing acceptance",
            "comparisons": comparisons}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = check(args.directory)
    text = json.dumps(result, indent=2) + "\n"
    (args.directory / "neutral-checked.json").write_text(text)
    print(text, end="")
    sys.exit(0 if result["neutralBindingPassed"] else 1)
