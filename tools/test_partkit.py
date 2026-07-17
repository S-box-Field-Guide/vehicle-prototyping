#!/usr/bin/env python3
"""test_partkit.py -- OFFLINE part-kit manifest validation gate (audit 2026-07-12 hardening).

WHY THIS EXISTS
  The C# `PartKitManifest.Validate` (Code/Vehicle/Parts/PartKitManifest.cs) is the ENFORCEMENT:
  at spawn it rejects any parseable-but-broken manifest and falls back to the fused/blockout body
  so a bad kit never bricks a spawn. That enforcement can't be exercised headlessly in this s&box
  template (no cheap C# unit-test runner). This script MIRRORS those rules in Python so the
  tools/gen_vehicle.py GENERATOR contract is guarded offline: it proves the two shipped manifests
  still pass and that a battery of malformed fixtures is correctly rejected.

  >>> LOCKSTEP <<<  validate_manifest() below mirrors PartKitManifest.Validate() rule-for-rule.
  If you change one, change the other. The C# side is authoritative at runtime; this is the
  offline guard for the generator + a regression net for the validator rules themselves.

WHAT IT CHECKS
  * Assets/models/vehicles/hatch_kit/manifest.json   (v1, shipped)  -> must PASS
  * Assets/models/vehicles/pickup_kit/manifest.json  (v2, shipped)  -> must PASS
  * tools/fixtures/partkit/ok_*.json                                -> must PASS
  * tools/fixtures/partkit/bad_*.json                               -> must be REJECTED

USAGE
  python tools/test_partkit.py            # run the whole battery (OFFLINE python mirror), print a table
  python tools/test_partkit.py --verbose  # also print each rejection's error list
  python tools/test_partkit.py --live      # ALSO feed all 32 fixtures through the REAL C# loader
                                            # (vp_validate_manifest McpTool) and compare accept/reject
  python tools/test_partkit.py --live --url http://127.0.0.1:7269/mcp

Exit code is non-zero if any expected-pass fails or any expected-reject passes, or (in --live) if the
C# loader disagrees with the python mirror on any fixture.
"""

import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mcp_client  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
FIXTURES = os.path.join(HERE, "fixtures", "partkit")
SHIPPED = [
    os.path.join(REPO, "Assets", "models", "vehicles", "hatch_kit", "manifest.json"),
    os.path.join(REPO, "Assets", "models", "vehicles", "pickup_kit", "manifest.json"),
    os.path.join(REPO, "Assets", "models", "vehicles", "coupe_kit", "manifest.json"),
    os.path.join(REPO, "Assets", "models", "vehicles", "kart_kit", "manifest.json"),
]

# ── contract constants — mirror PartKitManifest.cs ──────────────────────────────────────────
SCHEMA_V1 = "vp.partkit/1"
SCHEMA_V2 = "vp.partkit/2"
SCHEMA_V3 = "vp.partkit/3"   # adds the destruction damage band (D1)
WHEEL_NAMES = ("wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr")
RECOGNIZED_KINDS = {
    "chassis", "wheel", "door", "hood", "trunk", "tailgate", "bed", "bolton",
    "fascia", "mirror", "accessory",
}
RECOGNIZED_AXES = {"X", "Y", "Z"}
RECOGNIZED_ZONES = {"front", "hood", "rear", "door", "cabin", "wheel"}
OPTIONAL_BY_DEFAULT_KINDS = {"mirror", "accessory"}


def _finite3(a):
    """present, length-3, every element a finite real number (mirrors C# Finite3)."""
    if not isinstance(a, list) or len(a) != 3:
        return False
    for x in a:
        if not isinstance(x, (int, float)) or isinstance(x, bool):
            return False
        if not math.isfinite(x):
            return False
    return True


def _validate_part(kit, p, idx, errors):
    ident = f"'{p.get('part')}'" if isinstance(p.get("part"), str) and p.get("part").strip() else f"[index {idx}]"

    def bad(msg):
        errors.append(f"kit '{kit}' part {ident}: {msg}")

    vmdl = p.get("vmdl")
    if not (isinstance(vmdl, str) and vmdl.strip()):
        bad("empty 'vmdl'")

    kind = p.get("kind")
    if not (isinstance(kind, str) and kind.strip()):
        bad("empty 'kind'")
    elif kind not in RECOGNIZED_KINDS:
        bad(f"unrecognized kind '{kind}' (allowed: {', '.join(sorted(RECOGNIZED_KINDS))})")

    ax = p.get("rotation_axis_local")
    if ax is not None and ax not in RECOGNIZED_AXES:
        bad(f"unrecognized rotation_axis_local '{ax}' (allowed: null, X, Y, Z)")

    dims = p.get("dims_m")
    if not _finite3(dims):
        bad("dims_m must be a finite length-3 array")
    elif dims[0] <= 0 or dims[1] <= 0 or dims[2] <= 0:
        bad(f"dims_m must be positive, got {dims}")

    lo, hi = p.get("local_bounds_min_m"), p.get("local_bounds_max_m")
    lo_ok, hi_ok = _finite3(lo), _finite3(hi)
    if not lo_ok:
        bad("local_bounds_min_m must be a finite length-3 array")
    if not hi_ok:
        bad("local_bounds_max_m must be a finite length-3 array")
    if lo_ok and hi_ok:
        for a in range(3):
            if lo[a] > hi[a]:
                bad(f"local_bounds min > max on axis {a} ({lo[a]} > {hi[a]})")

    if not _finite3(p.get("attach_author_m")):
        bad("attach_author_m must be a finite length-3 array")

    al = p.get("attach_local_m")
    if al is not None and not _finite3(al):
        bad("attach_local_m, if present, must be a finite length-3 array")

    mf = p.get("mass_fraction")
    if not (isinstance(mf, (int, float)) and not isinstance(mf, bool) and math.isfinite(mf) and mf >= 0):
        bad(f"mass_fraction must be finite and non-negative, got {mf}")

    # ── schema v3 damage band — validated WHEN PRESENT (absent = loader fills a kind default,
    # per the versioning law; v1/v2 manifests omitting the band stay valid). ──
    def damage_field(name):
        v = p.get(name)
        if v is None:
            return
        if not (isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)) or v < 0:
            bad(f"{name} must be finite and non-negative, got {v}")
    # max_crush_m is legitimately 0 for wheels (never-dent sentinel), so non-negative not positive.
    for _f in ("dent_impulse", "loosen_impulse", "detach_impulse", "stiffness", "max_crush_m"):
        damage_field(_f)

    di, li, de = p.get("dent_impulse"), p.get("loosen_impulse"), p.get("detach_impulse")
    if isinstance(di, (int, float)) and isinstance(li, (int, float)) and not isinstance(di, bool) \
            and not isinstance(li, bool) and di > li:
        bad(f"dent_impulse ({di}) must be <= loosen_impulse ({li})")
    if isinstance(li, (int, float)) and isinstance(de, (int, float)) and not isinstance(li, bool) \
            and not isinstance(de, bool) and li > de:
        bad(f"loosen_impulse ({li}) must be <= detach_impulse ({de})")

    zone = p.get("zone")
    if zone is not None and zone not in RECOGNIZED_ZONES:
        bad(f"unrecognized zone '{zone}' (allowed: null, {', '.join(sorted(RECOGNIZED_ZONES))})")


def validate_manifest(manifest):
    """Return a list of error strings (empty == valid). Mirrors PartKitManifest.Validate."""
    errors = []
    if not isinstance(manifest, dict):
        return ["manifest: not a JSON object"]

    kit = manifest.get("kit")
    schema = manifest.get("schema")
    if schema not in (SCHEMA_V1, SCHEMA_V2, SCHEMA_V3):
        errors.append(f"kit '{kit}': schema '{schema}' not in ('{SCHEMA_V1}', '{SCHEMA_V2}', '{SCHEMA_V3}')")
    if not (isinstance(kit, str) and kit.strip()):
        errors.append("manifest: 'kit' name is empty")

    # optional citizen sit point (de-Kenney kart, 2026-07-13): additive field, no
    # schema bump (same law as 'required'); when present it must be finite-3
    ds = manifest.get("driver_seat_author_m")
    if ds is not None and not _finite3(ds):
        errors.append(f"kit '{kit}': driver_seat_author_m, if present, must be a finite length-3 array")

    parts = manifest.get("parts")
    if not isinstance(parts, list) or len(parts) == 0:
        errors.append(f"kit '{kit}': no parts")
        return errors

    seen = set()
    for i, p in enumerate(parts):
        if not isinstance(p, dict):
            errors.append(f"kit '{kit}': part [index {i}] is null")
            continue
        name = p.get("part")
        if not (isinstance(name, str) and name.strip()):
            errors.append(f"kit '{kit}': part [index {i}] has an empty 'part' name")
        elif name in seen:
            errors.append(f"kit '{kit}': duplicate part name '{name}'")
        else:
            seen.add(name)
        _validate_part(kit, p, i, errors)

    for wn in WHEEL_NAMES:
        c = sum(1 for p in parts if isinstance(p, dict) and p.get("part") == wn)
        if c != 1:
            errors.append(f"kit '{kit}': expected exactly one '{wn}', found {c}")

    for p in parts:
        if isinstance(p, dict) and p.get("kind") == "wheel" and p.get("part") not in WHEEL_NAMES:
            errors.append(f"kit '{kit}': part '{p.get('part')}' has kind 'wheel' but is not one of the FL/FR/RL/RR set")

    if not any(isinstance(p, dict) and p.get("kind") != "wheel" for p in parts):
        errors.append(f"kit '{kit}': has no body (non-wheel) parts")

    return errors


def _load(path):
    """Load JSON, allowing NaN/Infinity tokens (Python default) so those fixtures exercise the
    finite-checks. In C#, System.Text.Json instead THROWS on NaN/Infinity, which TryLoad's
    try/catch turns into the same null/reject outcome — both paths reject, which is the contract."""
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def collect_cases():
    """(label, path, expect_pass) for every shipped manifest + fixture."""
    cases = []
    for p in SHIPPED:
        cases.append((os.path.relpath(p, REPO).replace("\\", "/"), p, True))
    if os.path.isdir(FIXTURES):
        for fn in sorted(os.listdir(FIXTURES)):
            if not fn.endswith(".json"):
                continue
            cases.append(("fixtures/" + fn, os.path.join(FIXTURES, fn), fn.startswith("ok_")))
    return cases


def run_offline(cases, verbose):
    """The python-mirror battery. Returns (failures, results[(label, passed, errs)])."""
    failures = 0
    results = []
    print(f"{'result':7} {'expect':7} {'errs':4}  manifest")
    print("-" * 72)
    for label, path, expect_pass in cases:
        try:
            errs = validate_manifest(_load(path))
            passed = len(errs) == 0
        except Exception as e:  # a parse error is a reject (matches C# TryLoad try/catch -> null)
            errs = [f"parse error: {e}"]
            passed = False
        results.append((label, passed, errs))

        ok = (passed == expect_pass)
        if not ok:
            failures += 1
        mark = "OK  " if ok else "WRONG"
        verdict = "PASS" if passed else "REJECT"
        want = "PASS" if expect_pass else "REJECT"
        print(f"{mark:7} want={want:6} {len(errs):<4}  {label}"
              + ("" if ok else "   <-- MISMATCH"))
        if verbose and errs:
            for e in errs:
                print(f"           - {e}")

    print("-" * 72)
    total = len(cases)
    if failures:
        print(f"FAIL: {failures}/{total} manifests did not match expectation")
    else:
        print(f"PASS: all {total} manifests matched expectation "
              f"({sum(1 for c in cases if c[2])} pass / {sum(1 for c in cases if not c[2])} reject)")
    return failures


def _cs_validate(url, raw_text, timeout=30.0):
    """Feed RAW manifest text through the real C# PartKitManifest.FromJson via the vp_validate_manifest
    McpTool. Returns (accept: bool, reasons: [str]). Uses the 'json' arg (raw content) so there is no
    asset-mount timing dependency — it exercises the actual STJ bind + Validate() rules, which is where
    python/C# drift hides. Project [McpTool]s take one {"argsJson": "<json>"} param."""
    args = {"argsJson": json.dumps({"json": raw_text})}
    res = mcp_client.call(url, "vp_validate_manifest", args, timeout)
    txt = mcp_client._text_payload(res)
    if res.get("isError"):
        raise RuntimeError(f"vp_validate_manifest isError: {txt}")
    obj = json.loads(txt)
    if isinstance(obj, str):
        obj = json.loads(obj)
    if "error" in obj:
        raise RuntimeError(f"vp_validate_manifest error: {obj['error']}")
    return bool(obj.get("accept")), list(obj.get("reasons", []))


def run_live(cases, url, offline_results, verbose):
    """Feed every fixture through the REAL C# loader and compare accept/reject against both the
    expectation AND the python mirror. Returns the number of divergences."""
    # identity-probe: this must be the vehicle_prototyping editor with the harness loaded
    try:
        st = mcp_client.call(url, "editor_status", None, 15.0)
        stj = json.loads(mcp_client._text_payload(st))
        proj = stj.get("Project")
    except Exception as e:
        print(f"[live] cannot reach editor at {url}: {e}", file=sys.stderr)
        return 1
    if proj != "vehicle_prototyping":
        print(f"[live] editor at {url} is project '{proj}', not vehicle_prototyping — WRONG PORT.", file=sys.stderr)
        return 1

    by_label = {label: (passed, errs) for label, passed, errs in offline_results}

    print(f"\n[live] feeding {len(cases)} fixtures through the REAL C# loader (vp_validate_manifest) at {url}")
    print(f"{'result':7} {'C#':8} {'py':8}  manifest")
    print("-" * 72)
    divergences = 0
    for label, path, expect_pass in cases:
        with open(path, "r", encoding="utf-8") as f:
            raw = f.read()
        try:
            cs_accept, cs_reasons = _cs_validate(url, raw)
        except Exception as e:
            print(f"WRONG   {'ERR':8} {'':8}  {label}   <-- {e}")
            divergences += 1
            continue
        py_passed, py_errs = by_label.get(label, (None, []))
        agree = (cs_accept == py_passed) and (cs_accept == expect_pass)
        if not agree:
            divergences += 1
        mark = "OK  " if agree else "WRONG"
        print(f"{mark:7} {('accept' if cs_accept else 'reject'):8} "
              f"{('accept' if py_passed else 'reject'):8}  {label}"
              + ("" if agree else f"   <-- MISMATCH (want {'accept' if expect_pass else 'reject'})"))
        if verbose and cs_reasons:
            for r in cs_reasons:
                print(f"           C#- {r}")

    print("-" * 72)
    if divergences:
        print(f"LIVE FAIL: {divergences}/{len(cases)} fixtures diverged (C# loader vs python mirror / expectation)")
    else:
        print(f"LIVE PASS: all {len(cases)} fixtures agree (C# loader == python mirror == expectation)")
    return divergences


def main():
    verbose = "--verbose" in sys.argv or "-v" in sys.argv
    live = "--live" in sys.argv
    url = mcp_client.DEFAULT_URL
    if "--url" in sys.argv:
        i = sys.argv.index("--url")
        if i + 1 < len(sys.argv):
            url = sys.argv[i + 1]

    cases = collect_cases()
    if not cases:
        print("no manifests or fixtures found", file=sys.stderr)
        return 1

    failures = run_offline(cases, verbose)
    results = []
    # rebuild results for the live comparison (run_offline printed; recompute cheaply)
    for label, path, expect_pass in cases:
        try:
            errs = validate_manifest(_load(path))
            passed = len(errs) == 0
        except Exception as e:
            errs = [f"parse error: {e}"]
            passed = False
        results.append((label, passed, errs))

    rc = 1 if failures else 0
    if live:
        divergences = run_live(cases, url, results, verbose)
        if divergences:
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
