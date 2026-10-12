#!/usr/bin/env python3
"""Verify actual native AO input pixels against analytic planes, not counters.

This checks only the reverse-Z mode used by the game. It is not acceptance of
the town input policy, AO shading, total GPU cost, or sustained frame pacing.
The unlit control must lose normal-map tilt, proving this test detects the
specific regression that invalidated the first preparation timing.
"""
import argparse
import json
import math
from pathlib import Path
import struct


def check(directory):
    def read(name):
        path = directory / (name + "-reverseZ-true")
        colors = list(struct.iter_unpack("<4e", (path / "normals-coverage.rgba16f").read_bytes()))
        depths = list(struct.unpack("<65536f", (path / "depth.depth32f").read_bytes()))
        assert len(colors) == 256 * 256
        assert all(math.isfinite(v) for c in colors for v in c)
        assert all(math.isfinite(v) for v in depths)
        return colors, depths

    candidate, depth = read("preserve-source")
    reference, reference_depth = read("SCNLightingModelPhysicallyBased")
    unlit, unlit_depth = read("SCNLightingModelConstant")
    assert candidate == reference and depth == reference_depth, "Material copying changed the fixture"
    assert candidate[0] == (0, 0, 0, 0) and depth[0] == 0, "Background is an occluder"
    observations = []
    for index, (x, y, rgb, coverage, distance) in enumerate([
        (64, 64, (128, 128, 255), 1, 5),
        (192, 64, (204, 128, 230), 1, 5),
        (64, 192, (128, 204, 230), 1, 5),
        (192, 192, (128, 128, 255), .5, 4.75),
    ]):
        tangent = [v / 255 * 2 - 1 for v in rgb]
        length = math.sqrt(sum(v * v for v in tangent))
        expected = [(v / length * .5 + .5) * coverage for v in tangent] + [coverage]
        expected_depth = (20 - distance) / (20 - .1)
        maximum_error = 0
        for yy in range(y - 8, y + 9):
            for xx in range(x - 8, x + 9):
                pixel = yy * 256 + xx
                error = max(abs(a - b) for a, b in zip(candidate[pixel], expected))
                maximum_error = max(maximum_error, error)
                assert error < .002, f"Plane {index} mapped normal/coverage error: {error}"
                assert abs(depth[pixel] - expected_depth) < .000001, f"Plane {index} depth/deformation changed"
        if index in (1, 2):
            assert unlit[y * 256 + x][3] == 1 and unlit_depth[y * 256 + x] == depth[y * 256 + x], "Unlit control is not the same visible plane"
            assert max(abs(a - b) for a, b in zip(unlit[y * 256 + x], expected)) > .25, "Negative control did not expose missing normal maps"
        observations.append({"plane": index, "maximumColorError": maximum_error, "depth": depth[y * 256 + x]})
    overlap, overlap_depth = read("overlap-policy-true")
    broken, broken_depth = read("overlap-policy-false")
    assert overlap_depth == broken_depth, "Effect policy changed solid depth"
    for y in range(56, 73):
        for x in range(56, 73):
            receiver = overlap[y * 256 + x]
            for dx, dy in [(128, 0), (0, 128)]:
                pixel = (y + dy) * 256 + x + dx
                assert overlap[pixel] == receiver, "Color-only effect corrupted the receiver normal"
                assert abs(overlap_depth[pixel] - (20 - 5) / (20 - .1)) < .000001
                assert max(abs(a - b) for a, b in zip(broken[pixel], receiver)) > .1, "Overlap negative control failed"
            # Half-covered mapped ground must remain, interpolating two normals.
            dressing = overlap[(y + 128) * 256 + x + 128]
            a = reference[y * 256 + x + 128]
            b = reference[(y + 128) * 256 + x]
            assert max(abs(c - (v + w) * .5) for c, v, w in zip(dressing, a, b)) < .001
    perspective_errors = []
    tangent = [v / 255 * 2 - 1 for v in (204, 128, 230)]
    length = math.sqrt(sum(v * v for v in tangent))
    nx, ny, nz = [v / length for v in tangent]
    expected_normal = [(nx * math.cos(.35) + nz * math.sin(.35)) * .5 + .5,
                       ny * .5 + .5, (-nx * math.sin(.35) + nz * math.cos(.35)) * .5 + .5, 1]
    for distance in (5, 25, 120):
        name = f"perspective-{distance}"
        normals, depths = read(name)
        metadata = json.loads((directory / (name + "-reverseZ-true") / "preparation.json").read_text())
        projection = metadata["projectionColumnMajor"]
        maximum_error = 0
        for y in range(120, 137):
            for x in range(120, 137):
                pixel = y * 256 + x
                assert max(abs(a - b) for a, b in zip(normals[pixel], expected_normal)) < .002, "Rotated mapped normal changed"
                # SceneKit's projection is GL NDC; its reverse-Z attachment is
                # (1 - NDC.z)/2. Use actual float matrix coefficients. Rebuilding
                # from ideal near/far loses far-depth precision (~12 mm at 120 m).
                recovered = -projection[14] / (2 * depths[pixel] - 1 - projection[10])
                ray_x = ((x + .5) / 256 * 2 - 1) / projection[0]
                expected_distance = distance / (1 - ray_x * math.tan(.35))
                maximum_error = max(maximum_error, abs(recovered - expected_distance))
        assert maximum_error < max(.00002, distance * .000005), f"Perspective depth reconstruction error: {maximum_error}"
        perspective_errors.append({"distance": distance, "maximumDepthError": maximum_error})
    return {"fixturePassed": True, "gameplayAccepted": False, "reverseZ": True,
            "scope": "Analytic normal maps, fragment coverage, displaced depth, overlapping mapped dressing, multiply trails, airborne dust, rotated mapped normals and perspective depth; unlit and overlap negative controls",
            "observations": observations, "perspective": perspective_errors}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = check(args.directory)
    text = json.dumps(result, indent=2) + "\n"
    (args.directory / "checked.json").write_text(text)
    print(text, end="")
