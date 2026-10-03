#!/usr/bin/env python3
"""Certify triangles hidden inside Marvin's retained, convex Body components.

Requires Python 3.10+ and numpy. No CAD/graphics package, simulator, build, or
precomputed component file is needed. Only the output artifact directory is
written; the mechanical sources and runtime assets are never modified.

Example:
  python3 scripts/rendering/check-cad-enclosure.py --output /tmp/cad-enclosure
  python3 scripts/rendering/check-cad-enclosure.py --self-test

The proof concerns geometry: runtime Float32 positions are represented exactly
as integers, each closed Body component is checked against every supporting
plane, and each removed triangle fits entirely inside one convex component.
The minimum clearance is also checked with integer arithmetic. Rendering still
requires retained opaque enclosures, unchanged relative transforms, external
camera/light origins, and separate shader/shadow-map validation. Source hashes
bind those assumptions to the implementation inspected when the report ran;
they do not themselves prove runtime material or transform behavior.
"""

import argparse
import hashlib
import json
import math
from fractions import Fraction
from pathlib import Path

import numpy as np


# These parts and Body are direct children of Robot.root in the source inspected
# for this optimization. Moving head/neck groups and hidden CAD belts are omitted.
STATIC_PARTS = (
    "01_body", "02_track_cover_left", "03_track_cover_right", "04_wheel",
    "10_body_cover", "Buttons", "Lghts", "Motor_Left", "Motor_Right", "Servo_Head",
)


class InvalidEnclosure(ValueError):
    pass


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def exact_coordinates(values):
    """Return an exact integer coordinate array and its common binary scale."""
    values = np.asarray(values, dtype=np.float64)
    if not np.isfinite(values).all():
        raise ValueError("Non-finite coordinates")
    ratios = [[float(x).as_integer_ratio() for x in row] for row in values]
    scale = max(den for row in ratios for _, den in row)
    result = []
    for row, original in zip(ratios, values):
        integers = []
        for (num, den), value in zip(row, original):
            assert scale % den == 0
            integer = num * (scale // den)
            assert integer / scale == float(value)
            integers.append(integer)
        result.append(integers)
    return np.asarray(result, dtype=object), scale


def cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def body_components(vertices, faces):
    """Split actual runtime triangles by edges with exact position identity."""
    _, welded = np.unique(vertices, axis=0, return_inverse=True)
    indexed = welded[faces]
    adjacency = [set() for _ in faces]
    edges = {}
    for face_id, face in enumerate(indexed):
        for a, b in zip(face, np.roll(face, -1)):
            edges.setdefault(tuple(sorted((int(a), int(b)))), []).append(face_id)
    for owners in edges.values():
        for face_id in owners:
            adjacency[face_id].update(owners)
    remaining = set(range(len(faces)))
    result = []
    while remaining:
        pending = [min(remaining)]
        connected = set()
        while pending:
            item = pending.pop()
            if item in connected:
                continue
            connected.add(item)
            pending.extend(adjacency[item] - connected)
        remaining -= connected
        result.append(np.asarray(sorted(connected), dtype=np.int64))
    return result


def certify_shell(vertices, faces):
    """Reject open, inconsistently oriented, degenerate or nonconvex shells."""
    unique, welded = np.unique(vertices, axis=0, return_inverse=True)
    indexed = welded[faces]
    used = np.unique(indexed)
    if len(np.unique(np.sort(indexed, axis=1), axis=0)) != len(faces):
        raise InvalidEnclosure("Duplicate enclosure triangles")
    edges = {}
    for face in indexed:
        for a, b in zip(face, np.roll(face, -1)):
            a, b = int(a), int(b)
            if a == b:
                raise InvalidEnclosure("Degenerate enclosure edge")
            edges.setdefault(tuple(sorted((a, b))), []).append(a < b)
    if any(len(directions) != 2 for directions in edges.values()):
        raise InvalidEnclosure("Enclosure is open or has nonmanifold edges")
    if any(directions[0] == directions[1] for directions in edges.values()):
        raise InvalidEnclosure("Enclosure winding is inconsistent")
    if len(used) - len(edges) + len(faces) != 2:
        raise InvalidEnclosure("Enclosure is not a closed sphere topology")
    if len(body_components(unique, indexed)) != 1:
        raise InvalidEnclosure("Enclosure is disconnected")
    # Every vertex link must be one cycle, rejecting pinched/bow-tie vertices.
    for vertex in used:
        links = {}
        for face in indexed[np.any(indexed == vertex, axis=1)]:
            a, b = [int(x) for x in face if x != vertex]
            links.setdefault(a, set()).add(b)
            links.setdefault(b, set()).add(a)
        if any(len(neighbors) != 2 for neighbors in links.values()):
            raise InvalidEnclosure("Nonmanifold enclosure vertex")
        reached, pending = set(), [next(iter(links))]
        while pending:
            item = pending.pop()
            if item not in reached:
                reached.add(item)
                pending.extend(links[item] - reached)
        if len(reached) != len(links):
            raise InvalidEnclosure("Disconnected enclosure vertex link")

    ints, scale = exact_coordinates(unique)
    planes = []
    plane_ids = {}
    face_planes = []
    for face in indexed:
        a, b, c = (ints[i] for i in face)
        normal = cross(b-a, c-a)
        if normal == [0, 0, 0]:
            raise InvalidEnclosure("Zero-area enclosure triangle")
        offset = -dot(normal, a)
        if any(dot(normal, ints[i]) + offset > 0 for i in used):
            raise InvalidEnclosure("Enclosure is nonconvex or inward-facing")
        divisor = math.gcd(*normal, offset)
        normal = [int(x // divisor) for x in normal]
        offset = int(offset // divisor)
        key = (*normal, offset)
        if key not in plane_ids:
            plane_ids[key] = len(planes)
            planes.append((normal, offset))
        face_planes.append(plane_ids[key])
    normals = np.asarray([p[0] for p in planes], dtype=object)
    offsets = np.asarray([p[1] for p in planes], dtype=object)
    float_normals = np.asarray(normals, dtype=np.float64)
    lengths = np.linalg.norm(float_normals, axis=1)
    return {
        "integerScale": scale,
        "integerNormals": normals.tolist(),
        "integerOffsets": offsets.tolist(),
        "facePlaneIndices": face_planes,
        "vertexCount": len(used),
        "triangleCount": len(faces),
        "closedTwoFacesPerEdge": True,
        "consistentWinding": True,
        "connectedVertexLinks": True,
        "eulerCharacteristic": 2,
        "exactConvexSupportPlaneViolations": 0,
        "unitNormals": (float_normals / lengths[:, None]).tolist(),
        "unitOffsets": (np.asarray(offsets, dtype=np.float64) / (lengths*scale)).tolist(),
    }


def exact_inside(points, shell, margin):
    """Exact strict-side and >= margin checks for all points/all planes."""
    if not len(points):
        return np.zeros(0, dtype=bool)
    integers, point_scale = exact_coordinates(points)
    normals = np.asarray(shell["integerNormals"], dtype=object)
    offsets = np.asarray(shell["integerOffsets"], dtype=object)
    shell_scale = shell["integerScale"]
    norm_squared = np.sum(normals*normals, axis=1)
    required = norm_squared * point_scale**2 * shell_scale**2 * margin.numerator**2
    result = []
    for start in range(0, len(points), 256):
        q = (integers[start:start+256] @ normals.T)*shell_scale + offsets*point_scale
        satisfies = (q < 0) & (q*q*margin.denominator**2 >= required)
        result.extend(np.all(satisfies, axis=1).tolist())
    return np.asarray(result, dtype=bool)


def load_parts(directory):
    manifest_bytes = (directory / "manifest.json").read_bytes()
    binary = (directory / "geometry.bin").read_bytes()
    manifest = json.loads(manifest_bytes)
    parts = {}
    for p in manifest["parts"]:
        vc, fc = p["vertexCount"], p["triangleCount"]
        vo, io = p["vertexOffset"], p["indexOffset"]
        if min(vc, fc) <= 0 or min(vo, io) < 0 or vo+vc*24 > len(binary) or io+fc*12 > len(binary):
            raise ValueError(f"Invalid geometry range: {p['name']}")
        if p["name"] in parts:
            raise ValueError(f"Duplicate part name: {p['name']}")
        vertices = np.frombuffer(binary, dtype="<f4", count=vc*6, offset=vo).reshape(-1, 6)[:, :3].astype(np.float64)
        faces = np.frombuffer(binary, dtype="<u4", count=fc*3, offset=io).reshape(-1, 3)
        if faces.max() >= vc or not np.isfinite(vertices).all():
            raise ValueError(f"Invalid geometry values: {p['name']}")
        parts[p["name"]] = (vertices, faces)
    return manifest, manifest_bytes, binary, parts


def run(root, output, margin_mm):
    directory = root / "apps/simulator-macos/Resources/Marvin"
    manifest, manifest_bytes, binary, parts = load_parts(directory)
    units = Fraction(str(manifest["millimetersPerUnit"]))
    if units <= 0 or margin_mm <= 0:
        raise ValueError("Units and clearance must be positive")
    margin = margin_mm / units
    body_vertices, body_faces = parts["Body"]
    shells = []
    for component_id, face_ids in enumerate(body_components(body_vertices, body_faces)):
        shell = certify_shell(body_vertices, body_faces[face_ids])
        shell.update({"part": "Body", "component": component_id, "faceIndices": face_ids.tolist()})
        shells.append(shell)
    if not shells:
        raise InvalidEnclosure("No Body enclosure components")

    output.mkdir(parents=True, exist_ok=True)
    selections = {}
    records = []
    for name in STATIC_PARTS:
        vertices, faces = parts[name]
        union = np.zeros(len(faces), dtype=bool)
        by_shell = []
        for component_id, shell in enumerate(shells):
            normals = np.asarray(shell["unitNormals"])
            offsets = np.asarray(shell["unitOffsets"])
            distances = np.full(len(vertices), -np.inf)
            for start in range(0, len(vertices), 4096):
                distances[start:start+4096] = (vertices[start:start+4096] @ normals.T + offsets).max(axis=1)
            # Broad prefilter only. All acceptance, including the margin, is exact.
            possible = distances <= -float(margin) + 1e-12
            ids = np.flatnonzero(possible)
            inside = np.zeros(len(vertices), dtype=bool)
            inside[ids] = exact_inside(vertices[ids], shell, margin)
            mask = inside[faces].all(axis=1)
            selected = np.flatnonzero(mask)
            union |= mask
            key = f"{name}_insideBody{component_id}"
            selections[key] = selected
            by_shell.append({
                "component": component_id, "indicesKey": key,
                "triangleCount": len(selected), "faceIndices": selected.tolist(),
                "exactStrictHalfspaceProof": True,
                "exactMinimumClearanceMM": str(margin_mm),
            })
        selections[f"{name}_remove"] = np.flatnonzero(union)
        records.append({"part": name, "sourceTriangles": len(faces),
                        "containedTriangles": int(union.sum()), "byShell": by_shell})
        print(f"{name}: {int(union.sum()):,} / {len(faces):,} triangles enclosed", flush=True)
    # The only certifying geometry is Body; it is never part of a removal set.
    selections["Body_retain"] = np.arange(len(body_faces), dtype=np.int64)
    index_path = output / "enclosure-indices.npz"
    np.savez_compressed(index_path, **selections)
    source_paths = ["apps/simulator-macos/Sources/MarvinSimulator/Robot.swift",
                    "apps/simulator-macos/Sources/MarvinSimulator/DirtCoating.swift"]
    source_hashes = {p: sha256((root / p).read_bytes()) for p in source_paths}
    report = {
        "schemaVersion": 1, "geometrySHA256": sha256(binary),
        "manifestSHA256": sha256(manifest_bytes),
        "sourceHashes": source_hashes,
        "analysisScriptSHA256": sha256(Path(__file__).read_bytes()),
        "indicesFile": index_path.name, "indicesNPZSHA256": sha256(index_path.read_bytes()),
        "minimumClearanceMM": str(margin_mm), "millimetersPerUnit": str(units),
        "exactMarginPredicate": "For point integers P at scale T and face (n,d) at scale S, Q=(n dot P)*S+d*T must be negative and Q^2*mDen^2 >= (n dot n)*T^2*S^2*mNum^2, where mNum/mDen is clearance in scene units.",
        "scope": "Geometric containment only. Full triangle follows because all its vertices lie inside one certified convex shell. Every Body triangle is retained. No views, ray samples, hole filling, or approximate mesh repair.",
        "runtimeConditions": ["Body remains opaque and undeformed", "Candidate and Body share their original rigid transform", "Camera and light origins remain outside Body", "Rendering and shadow-map equivalence require separate validation"],
        "shells": shells, "parts": records,
        "totalContainedTriangles": sum(p["containedTriangles"] for p in records),
    }
    (output / "enclosure-certificate.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Certified total: {report['totalContainedTriangles']:,}; report: {output / 'enclosure-certificate.json'}")


def self_test():
    vertices = np.asarray([[-1,-1,-1], [1,-1,-1], [1,1,-1], [-1,1,-1],
                           [-1,-1,1], [1,-1,1], [1,1,1], [-1,1,1]], dtype=float)
    faces = np.asarray([[0,2,1], [0,3,2], [4,5,6], [4,6,7], [0,1,5], [0,5,4],
                        [1,2,6], [1,6,5], [2,3,7], [2,7,6], [3,0,4], [3,4,7]])
    shell = certify_shell(vertices, faces)
    points = np.asarray([[0,0,0], [.75,0,0], [.875,0,0], [1,0,0], [1.25,0,0]])
    assert exact_inside(points, shell, Fraction(1,4)).tolist() == [True, True, False, False, False]
    # Mixed binary scales must not truncate non-integral coordinates.
    values = np.asarray([[np.float32(.1), np.float32(.003), np.float32(-.07)]], dtype=np.float64)
    integers, scale = exact_coordinates(values)
    assert np.asarray(integers, dtype=np.float64).tolist() == (values*scale).tolist()
    # Triangle acceptance requires every vertex, not a contained centroid.
    mixed = np.asarray([[0,0,0], [.1,0,0], [1.25,0,0]])
    assert not exact_inside(mixed, shell, Fraction(1,4)).all()
    assert exact_inside(mixed.mean(axis=0)[None], shell, Fraction(1,4)).all()
    # Exact 3-4-5 rotation plus scale: the oblique plane normal has length 5,
    # rather than 1. Shell integers use scale 8; these point integers use 32.
    # The near face is 5/8 from the centre, so t=1/2 leaves 5/16 clearance
    # (passes 1/4), t=3/4 leaves 5/32 (fails), and t>=1 is not strictly inside.
    oblique_transform = np.asarray([[3,-4,0], [4,3,0], [0,0,5]]) / 8
    oblique = certify_shell(vertices @ oblique_transform.T, faces)
    oblique_points = np.asarray([[t,0,0] for t in [0, .5, .75, 1, 1.25]]) @ oblique_transform.T
    _, oblique_point_scale = exact_coordinates(oblique_points)
    assert oblique["integerScale"] == 8 and oblique_point_scale == 32
    assert exact_inside(oblique_points, oblique, Fraction(1,4)).tolist() == [True, True, False, False, False]
    failures = []
    dented = vertices.copy(); dented[6] = [.25, .25, .25]
    for label, v, f in [("open", vertices, faces[:-1]), ("nonconvex", dented, faces),
                        ("inward", vertices, faces[:, ::-1]),
                        ("duplicate", vertices, np.vstack((faces, faces[0]))),
                        ("inconsistent winding", vertices, np.vstack((faces[0, ::-1], faces[1:])) )]:
        try:
            certify_shell(v, f)
        except InvalidEnclosure:
            failures.append(label)
        else:
            raise AssertionError(f"Unsafe enclosure accepted: {label}")
    doubled = np.vstack((vertices, vertices + [4,0,0]))
    doubled_faces = np.vstack((faces, faces + len(vertices)))
    assert len(body_components(doubled, doubled_faces)) == 2
    print(json.dumps({"selfTest": "passed", "rejectedShellFixtures": failures,
                      "pointFixtures": ["inside", "exact margin", "insufficient margin", "boundary", "outside"],
                      "otherFixtures": ["mixed coordinate scales", "oblique shell and unequal non-unit scales", "outside triangle vertex", "two components"]}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--margin-mm", default="0.01", help="Positive exact decimal clearance in CAD millimeters")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    if args.output:
        run(args.root.resolve(), args.output.resolve(), Fraction(args.margin_mm))
    elif not args.self_test:
        parser.error("--output is required for asset analysis")


if __name__ == "__main__":
    main()
