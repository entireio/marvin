"""Helpers shared by the CI scripts in tools/ci (standard library only; .github/workflows/godot-windows.yml).

Reports are compared as text: JSON numbers keep their written digits (no float round trip), so a last-bit difference
shows as a differing key. Large text dumps are compared through digests (per record hashes) committed in
tools/ci/reference, so the references stay small.
"""
import hashlib, json, os, re
from decimal import Decimal, InvalidOperation
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent          # apps/simulator-godot
CI = ROOT / "tools" / "ci"
REFERENCE = CI / "reference"


def read_text(path):
    """A text file with Windows line endings normalised (Console.WriteLine and Python text mode write CRLF there)."""
    return Path(path).read_bytes().decode("utf-8", errors="replace").replace("\r\n", "\n")


# ---------------------------------------------------------------- JSON

def load_json(path):
    """JSON with every number kept as its written text (a str subclass), so 0.1 and 0.10000000000000001 differ."""
    return json.loads(read_text(path), parse_float=Number, parse_int=Number)


class Number(str):
    """A JSON number as written."""


def flatten(value, prefix="", out=None):
    """{"a": {"b": [1, 2]}} -> {"a.b[0]": 1, "a.b[1]": 2}; empty containers are kept as values."""
    out = {} if out is None else out
    if isinstance(value, dict) and value:
        for key in value:
            flatten(value[key], f"{prefix}.{key}" if prefix else str(key), out)
    elif isinstance(value, list) and value:
        for i, item in enumerate(value):
            flatten(item, f"{prefix}[{i}]", out)
    else:
        out[prefix] = value
    return out


def show(value):
    if value is MISSING: return "(missing)"
    if isinstance(value, Number): return str(value)
    return json.dumps(value, ensure_ascii=False)


MISSING = object()


def relative_difference(a, b):
    try:
        x, y = Decimal(str(a)), Decimal(str(b))
    except (InvalidOperation, ValueError):
        return None
    if x == y: return Decimal(0)
    scale = max(abs(x), abs(y))
    return abs(x - y) / scale if scale else None


def compare_json(reference, actual, ignore=()):
    """Key-by-key comparison of two JSON documents. Returns (same_keys, differences) where differences is a list of
    (key, reference value, actual value, relative difference or None)."""
    ref, act = flatten(reference), flatten(actual)
    keys = list(ref) + [k for k in act if k not in ref]
    same, differences = 0, []
    for key in keys:
        if any(re.fullmatch(pattern, key) for pattern in ignore):
            continue
        a, b = ref.get(key, MISSING), act.get(key, MISSING)
        if a is not MISSING and b is not MISSING and type(a) is type(b) and a == b:
            same += 1
            continue
        rel = relative_difference(a, b) if isinstance(a, Number) and isinstance(b, Number) else None
        differences.append((key, a, b, rel))
    return same, differences


# ---------------------------------------------------------------- text dumps

def records(text):
    """A dump's records: each top-level line with the indented lines that follow it."""
    out = []
    for line in text.split("\n"):
        if not line:
            continue
        if line[0] in " \t" and out:
            out[-1].append(line)
        else:
            out.append([line])
    return out


def record_kind(record):
    return record[0].split(" ", 1)[0]


def short_hash(lines):
    return hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()[:8]


def digest(text):
    """{kind: {"count", "sha256", "hashes"}}: per record kind the number of records, the hash of all of them and an
    8-hex-digit hash per record (concatenated), enough to say which records differ without the reference text."""
    kinds = {}
    for record in records(text):
        kind = kinds.setdefault(record_kind(record), {"records": []})
        kind["records"].append(record)
    result = {}
    for name, kind in kinds.items():
        recs = kind["records"]
        result[name] = {
            "count": len(recs),
            "sha256": hashlib.sha256("\n".join("\n".join(r) for r in recs).encode("utf-8")).hexdigest(),
            "hashes": "".join(short_hash(r) for r in recs),
        }
    return {"sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(), "kinds": result}


def compare_digest(reference, text):
    """Compares a dump with a reference digest. Returns (identical, rows): one row per record kind,
    (kind, reference count, actual count, differing record indices, first differing actual records)."""
    actual = digest(text)
    if actual["sha256"] == reference["sha256"]:
        return True, []
    by_kind = {}
    for record in records(text):
        by_kind.setdefault(record_kind(record), []).append(record)
    rows = []
    for name in list(reference["kinds"]) + [k for k in actual["kinds"] if k not in reference["kinds"]]:
        ref = reference["kinds"].get(name)
        act = actual["kinds"].get(name)
        if ref and act and ref["sha256"] == act["sha256"]:
            continue
        ref_hashes = [ref["hashes"][i:i + 8] for i in range(0, len(ref["hashes"]), 8)] if ref else []
        act_hashes = [act["hashes"][i:i + 8] for i in range(0, len(act["hashes"]), 8)] if act else []
        differing = [i for i in range(max(len(ref_hashes), len(act_hashes)))
                     if i >= len(ref_hashes) or i >= len(act_hashes) or ref_hashes[i] != act_hashes[i]]
        examples = [" | ".join(by_kind[name][i]) for i in differing[:3] if name in by_kind and i < len(by_kind[name])]
        rows.append((name, ref["count"] if ref else 0, act["count"] if act else 0, differing, examples))
    return False, rows


# ---------------------------------------------------------------- exit codes

NTSTATUS = {0xC0000005: "access violation", 0xC0000374: "heap corruption", 0xC0000409: "fail fast",
            0xC00000FD: "stack overflow", 0xC0000142: "DLL initialisation failed", 0xE0434352: ".NET exception"}


def is_crash(code):
    """A Windows exception status (0xC0000000 and up) rather than an exit code the program chose."""
    return code is not None and (code & 0xFFFFFFFF) >= 0xC0000000


def exit_text(code, timed_out=False):
    if timed_out:
        return "timeout"
    if code is None:
        return "-"
    if is_crash(code):
        c = code & 0xFFFFFFFF
        return f"{c:#010x} ({NTSTATUS.get(c, 'crash')})"
    return str(code)


# ---------------------------------------------------------------- output

def md_escape(text, limit=160):
    text = str(text).replace("|", "\\|").replace("\n", " ")
    return text if len(text) <= limit else text[:limit - 1] + "…"


def append_summary(markdown):
    """Appends to the job summary (GITHUB_STEP_SUMMARY) when running in Actions, and prints it."""
    print(markdown)
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(markdown + "\n")


def write_json(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False, default=str) + "\n", encoding="utf-8")
