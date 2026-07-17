#!/usr/bin/env python3
"""vp_test.py -- the vehicle_prototyping REGRESSION GATE.

Runs the scripted maneuver battery against the live s&box editor and prints a
verdict table. `python tools/vp_test.py --all` must be green before any change
lands. Talks to the editor through tools/mcp_client.py on this project's assigned
port (vehicle_prototyping = 7290 -- NEVER another project's port).

WHAT IT DOES (per maneuver spec in specs/maneuvers/*.json):
  1. identity-probe        editor_status.Project == "vehicle_prototyping"
                           AND search_tools 'vp_' lists the harness tools.
  2. compile gate          compile_status must be Success + Errors=0; refuse to
                           run on a stale/wedged compile (success=False + 0
                           diagnostics + needsBuild=False is the WEDGE signature
                           -- bump a source mtime to recover, see docs).
  3. play_start
  4. vp_spawn {station,car}          seat the car at the station.
  5. vp_drive {op:"maneuver", ...}   kick off the scripted run.
  6. poll vp_drive {op:"status"}     until telemetry status == "done".
  7. evaluate asserts                against the returned telemetry JSON.
  8. play_stop

The vp_spawn / vp_drive / vp_status / vp_audit [McpTool]s are the C# HARNESS
(see docs/testing-harness.md for their contract). If that harness assembly is not
loaded in the editor, this runner DEGRADES GRACEFULLY:
  * LIVE mode: if search_tools 'vp_' is empty (or a vp_ call returns
    tool-not-found), it fails every run with a clear
    "harness C# not loaded" message -- never a cryptic traceback.
  * --dry-run: validates the spec files fully OFFLINE (schema, ops, metric names
    against the frozen telemetry contract). No editor required. This is the
    CI-safe gate that proves the specs are well-formed.

USAGE
  python tools/vp_test.py --dry-run --all         # offline spec validation (the CI-safe gate)
  python tools/vp_test.py --dry-run specs/maneuvers/launch.json
  python tools/vp_test.py --all                   # LIVE battery (needs editor + C# harness)
  python tools/vp_test.py specs/maneuvers/skidpad.json
  python tools/vp_test.py --all --json            # + machine-readable summary

Exit code is non-zero if any run FAILs or any spec is INVALID -- so `--all` is the
merge gate.

MANEUVER SPEC FILE FORMAT  (one JSON object per file; a bare list of objects and
a {"suite":[...]} wrapper are also accepted, so a file may hold several runs)
  {
    "maneuver": "launch",            # required: a battery name (see MANEUVERS)
    "car":      "hatch",             # required: CarDefinition id
    "station":  "dragstrip",         # required: proving-ground station id (see STATIONS)
    "params":   { ... },             # optional: maneuver-specific inputs passed to the pilot
    "asserts": [                     # required: pass conditions on telemetry fields
      {"metric": "zeroToHundredS", "op": "<=", "value": 12.0},
      {"metric": "wheelspinS",     "op": "<=", "value": 3.0},
      {"metric": "coneStrikes",    "op": "==", "value": 0},
      {"metric": "lateralGAvg",    "op": "between", "value": [0.5, 1.3]},
      {"metric": "catchable",      "op": "==", "value": true}
    ]
  }

  op       one of  <=  >=  ==  between .
  value    a number for <= >= == ; a bool for == ; a [lo, hi] pair for between .
  metric   MUST be a field of the frozen telemetry contract (KNOWN_METRICS below /
           docs/testing-harness.md). Unknown metric names are a dry-run ERROR so
           specs can't silently drift from the C# telemetry the runner consumes.

Assert VALUES in the shipped specs are GENEROUS PLACEHOLDER bounds marked
"PLACEHOLDER -- tighten from docs/handling-targets.md": the real per-class
bands are drafted in docs/handling-targets.md.
"""

import argparse
import json
import os
import sys
import time

try:
    sys.stdout.reconfigure(encoding="utf-8")   # Windows cp1252 mojibakes tool payloads
except Exception:
    pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mcp_client  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANEUVER_DIR = os.path.join(ROOT, "specs", "maneuvers")

# ── The battery names. A spec's "maneuver" must be one of these. ──────────
MANEUVERS = {
    "launch", "topspeed", "brake", "skidpad", "slalom", "jturn", "liftoff",
    "jump", "washboard", "hillclimb", "figure8", "driftexit", "spinrecovery",
}

# ── Maneuvers that are out of scope for this kit. A spec naming one is REJECTED at dry-run with a
#    clear message: full crash/destruction simulation is not part of this prototyping kit, so a
#    reintroduced/copied crash spec must fail OFFLINE here rather than pass validation and then die
#    live as "unknown maneuver 'crash'". ──────────────────────────────────────────
MIGRATED = {
    "crash": "out of scope for this kit (no destruction simulation)",
}

# ── Proving-ground station ids (see docs/proving-grounds.md). Advisory: unknown stations warn
#    (the station registry is authored in the C# harness), they don't fail. ─
STATIONS = {
    "dragstrip", "brakezone", "skidpad", "slalom", "ramps", "openpad",
    "washboard", "hillgrade", "bankedcurve", "lowgrip", "city",
    # authoritative TestTrack.Stations keys the specs may also use directly (wave-2
    # reconcile: StationCarRegistry aliases hillgrade->hillclimb, openpad/jturn->jturnpad,
    # so BOTH spellings resolve live).
    "hillclimb", "jturnpad",
    # crashwall_reserved: the crash plot is a RESERVED, reference-only station — the `crash` maneuver
    # is out of scope for this kit, so no spec here consumes it, but TestTrack still builds the plot.
    "crashwall", "crashwall_reserved",
}

# ── The FROZEN telemetry contract (docs/testing-harness.md is the interface
#    freeze). Every field the runner may assert on / read from vp_drive's
#    telemetry JSON. Keep this in lockstep with the C# TelemetryReport and the
#    doc. Unknown assert metrics are a validation error. ────────────────────────
KNOWN_METRICS = {
    # kinematics
    "speedMs", "maxSpeedMs", "distanceM", "elapsedS",
    # accelerations (g)
    "lateralGPeak", "lateralGAvg", "longGPeak", "longGAvg",
    # rotation / attitude (deg, deg/s)
    "yawRatePeakDeg", "yawRateAvgDeg", "pitchDeg", "rollDeg", "headingDriftDeg",
    # drivetrain
    "gear", "gearAtVmax", "rpm", "wheelspinS",
    # maneuver-derived
    "zeroToHundredS", "brakeDistanceM", "lockupTicks", "airtimeS",
    "landingPitchDeg", "landingRollDeg", "settleS", "jturnTimeS",
    "yawOvershootDeg", "coneStrikes", "contactLossPct", "wheelContactLossPct",
    "rollbackM", "exitRecoveryS", "speedRetention", "peakSlipDeg", "recoveryS",
    # booleans
    "catchable", "spunOut", "climbed",
    # standing invariant audits (target 0)
    "flips", "flippedTicks", "fallThroughs", "stuckTicks", "nanForces",
    "sleepWhileDriving",
}

OPS = {"<=", ">=", "==", "between"}
POLL_TIMEOUT_S = 90.0     # max wall time to wait for a maneuver run to finish
POLL_INTERVAL_S = 0.5


class VpError(Exception):
    pass


# ───────────────────────────── spec validation ─────────────────────────────

def validate_entry(entry, where):
    """Return a list of problem strings for one maneuver entry ([] == valid)."""
    problems = []
    if not isinstance(entry, dict):
        return [f"{where}: entry is not a JSON object"]

    man = entry.get("maneuver")
    if not isinstance(man, str) or not man:
        problems.append(f"{where}: missing/invalid 'maneuver' (string)")
    elif man in MIGRATED:
        problems.append(f"{where}: maneuver '{man}' {MIGRATED[man]}")
    elif man not in MANEUVERS:
        problems.append(f"{where}: unknown maneuver '{man}' (not in the battery {sorted(MANEUVERS)})")

    if not isinstance(entry.get("car"), str) or not entry.get("car"):
        problems.append(f"{where}: missing/invalid 'car' (string)")

    station = entry.get("station")
    if not isinstance(station, str) or not station:
        problems.append(f"{where}: missing/invalid 'station' (string)")

    if "params" in entry and not isinstance(entry["params"], dict):
        problems.append(f"{where}: 'params' must be an object")

    asserts = entry.get("asserts")
    if not isinstance(asserts, list) or not asserts:
        problems.append(f"{where}: 'asserts' must be a non-empty list")
        return problems

    for i, a in enumerate(asserts):
        aw = f"{where} assert[{i}]"
        if not isinstance(a, dict):
            problems.append(f"{aw}: not an object")
            continue
        metric = a.get("metric")
        op = a.get("op")
        val = a.get("value")
        if not isinstance(metric, str) or not metric:
            problems.append(f"{aw}: missing 'metric'")
        elif metric not in KNOWN_METRICS:
            problems.append(f"{aw}: metric '{metric}' not in the telemetry contract "
                            f"(docs/testing-harness.md) -- spec/contract drift")
        if op not in OPS:
            problems.append(f"{aw}: op '{op}' not in {sorted(OPS)}")
        # value shape per op
        if op == "between":
            if not (isinstance(val, list) and len(val) == 2
                    and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in val)):
                problems.append(f"{aw}: 'between' needs value [lo, hi] of two numbers")
            elif val[0] > val[1]:
                problems.append(f"{aw}: 'between' lo > hi ({val[0]} > {val[1]})")
        elif op == "==":
            if not isinstance(val, (int, float, bool)):
                problems.append(f"{aw}: '==' needs a number or bool value")
        elif op in ("<=", ">="):
            if not isinstance(val, (int, float)) or isinstance(val, bool):
                problems.append(f"{aw}: '{op}' needs a numeric value")
    return problems


def load_entries(path):
    """A spec file -> list of maneuver entries. Accepts a single object, a bare
    list, or a {"suite":[...]} / {"runs":[...]} wrapper."""
    with open(path, "r", encoding="utf-8") as f:
        doc = json.load(f)
    if isinstance(doc, list):
        return doc
    if isinstance(doc, dict):
        if isinstance(doc.get("suite"), list):
            return doc["suite"]
        if isinstance(doc.get("runs"), list):
            return doc["runs"]
        return [doc]
    raise VpError(f"{path}: top-level JSON must be object or list")


# ───────────────────────────── assert evaluation ─────────────────────────────

def _num(x):
    return isinstance(x, (int, float)) and not isinstance(x, bool)


def eval_assert(a, telem):
    """(passed: bool, detail: str). Missing metric == fail (contract not met)."""
    metric, op, val = a["metric"], a["op"], a.get("value")
    if metric not in telem:
        return False, f"{metric} MISSING from telemetry"
    got = telem[metric]
    if op == "<=":
        ok = _num(got) and got <= val
    elif op == ">=":
        ok = _num(got) and got >= val
    elif op == "==":
        ok = (got == val)
    elif op == "between":
        ok = _num(got) and val[0] <= got <= val[1]
    else:
        return False, f"{metric} unknown op {op}"
    shown = val if op != "between" else f"[{val[0]},{val[1]}]"
    return bool(ok), f"{metric}={got} {op} {shown}"


# ───────────────────────────── MCP plumbing ─────────────────────────────

def mcp(url, tool, args=None, timeout=60.0):
    """Call a tool; return (result, text). Raises VpError on isError so callers
    can classify (a not-found vp_ tool == harness not landed)."""
    res = mcp_client.call(url, tool, args, timeout)
    txt = mcp_client._text_payload(res)
    if res.get("isError"):
        raise VpError(txt or f"{tool} returned isError")
    return res, txt


def mcp_json(url, tool, args=None, timeout=60.0):
    _, txt = mcp(url, tool, args, timeout)
    try:
        obj = json.loads(txt)
    except ValueError:
        raise VpError(f"{tool} returned non-JSON: {txt[:200]}")
    if isinstance(obj, str):        # defensive: one extra encode layer
        try:
            obj = json.loads(obj)
        except ValueError:
            pass
    if isinstance(obj, dict) and "error" in obj:
        raise VpError(f"{tool} error: {obj['error']}")
    return obj


HARNESS_MISSING = (
    "harness C# not loaded -- vp_ [McpTool]s (vp_status/vp_spawn/vp_drive/vp_audit) "
    "are not registered in this editor. Make sure the project's editor assembly is "
    "compiled and loaded; see docs/testing-harness.md for the contract. Run with "
    "--dry-run to validate specs offline."
)


# ── play-transition hardening: stop-and-verify between runs + retry-with-backoff
#    on "Already playing". A leftover play session from OUR own prior run is stopped and
#    verified; a race-lost "Already playing" is NEVER answered with a blind play_stop
#    (that would kill an in-flight run when the editor's play mode is shared),
#    only backed off and retried. ──────────────────────────────────────────────────────
def _is_playing(url):
    try:
        st = mcp_json(url, "editor_status")
        return bool(st.get("IsPlaying"))
    except VpError:
        return False


def ensure_stopped(url, timeout=20.0):
    """If the editor is playing (leftover from our own prior run that didn't stop cleanly),
    stop it and poll editor_status until it confirms stopped. We own play mode exclusively
    this session, so a play session here is ours to clean up."""
    if not _is_playing(url):
        return True
    try:
        mcp(url, "play_stop", timeout=60.0)
    except VpError:
        pass
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not _is_playing(url):
            return True
        time.sleep(0.5)
    return not _is_playing(url)


def _harness_ready(url):
    """After a play transition the vp_ bridge tools can be transiently unregistered for a
    tick or two. Probe vp_drive status; True once it answers (any JSON), False on the
    transient tool-not-found."""
    try:
        mcp_json(url, "vp_drive", _args_json({"op": "status"}), timeout=30.0)
        return True
    except VpError:
        return False


def robust_play_start(url, attempts=6):
    """Stop-and-verify, then play_start with retry-with-backoff on 'Already playing', then
    wait for the vp_ harness to respond before returning. Raises VpError only after
    exhausting retries or on a non-contention error."""
    ensure_stopped(url)
    backoff = 1.0
    for _ in range(attempts):
        try:
            mcp(url, "play_start", timeout=120.0)
            break
        except VpError as e:
            if "already playing" in str(e).lower():
                time.sleep(backoff)
                backoff = min(backoff * 2, 8.0)
                # someone (or our own not-yet-settled stop) still holds play; re-verify our
                # own leftover only, never preempt — ensure_stopped is a no-op if not playing
                continue
            raise
    else:
        raise VpError("play_start failed after retries (persistent 'Already playing')")
    # readiness gate: poll until the bridge tools answer (transient post-transition gap)
    deadline = time.time() + 15.0
    while time.time() < deadline:
        if _harness_ready(url):
            return
        time.sleep(0.5)
    # proceed anyway; the spawn call will surface a clear error if truly missing


def preflight(url):
    """Identity-probe (vehicle_prototyping on this port) + confirm the vp_ harness
    is present. Returns (status_dict, harness_present: bool)."""
    st = mcp_json(url, "editor_status")
    proj = st.get("Project")
    if proj != "vehicle_prototyping":
        raise VpError(f"editor on {url} is project '{proj}', not vehicle_prototyping "
                      f"-- WRONG PORT (vehicle_prototyping=7290). Aborting.")
    # search_tools 'vp_' proves the harness assembly is loaded (identity beyond
    # the project name).
    harness = False
    try:
        _, txt = mcp(url, "search_tools", {"query": "vp_"})
        harness = ("vp_drive" in txt) or ("vp_status" in txt)
    except VpError:
        harness = False
    return st, harness


def referenced_stations(files):
    """The set of station ids referenced by the selected spec files (for the measurement-world gate)."""
    stations = set()
    for path in files:
        try:
            for e in load_entries(path):
                s = e.get("station")
                if isinstance(s, str) and s:
                    stations.add(s)
        except (ValueError, VpError):
            pass  # a malformed spec surfaces as a per-entry validation failure elsewhere
    return stations


def measurement_world_gate(url, files):
    """FAIL CLOSED unless the editor is in the proto measurement world (audit 2026-07-13 HIGH).

    Reads vp_status.world — the world built this play session, or the pending `vp_world` ConVar before
    play. A persisted `vp_world playground` leaves the proto TestTrack (and its stations) UNBUILT, and
    the old runner would then silently measure against free-drive geometry. We refuse here with a precise
    message. When the world is already built (a re-run), we also cross-check that every station the
    selected specs reference is in the proto registry; otherwise the per-run stationResolved check in
    execute_run is the always-on guarantee."""
    st = mcp_json(url, "vp_status")
    world = str(st.get("world", "")).lower()
    if world != "proto":
        raise VpError(
            f"measurement world is '{st.get('world')}', not 'proto'. The battery measures against the "
            f"proto TestTrack; a persisted `vp_world playground` leaves the test track and its stations "
            f"unbuilt. Set `vp_world proto` in the editor console before Play, then rerun.")
    if st.get("worldBuilt"):
        census = {str(s).lower() for s in st.get("stations", [])}
        missing = sorted(s for s in referenced_stations(files) if s.lower() not in census)
        if missing:
            raise VpError(
                f"stations referenced by the selected specs are absent from the proto TestTrack "
                f"registry: {missing} (present: {sorted(census)}). Refusing to run against a partial world.")
    return st


def compile_gate(url):
    """Refuse to run on a stale/wedged compile (gotchas.md Tooling). Raises VpError
    with the recovery hint if the compile is not clean-and-fresh."""
    cs = mcp_json(url, "compile_status")
    # Real engine shape (verified live, 26.07.08e): {IsBuilding, Compilers:[{Name,
    # IsBuilding, NeedsBuild, Success, Errors, Warnings, Diagnostics}]}. Reduce the
    # per-compiler array; keep the defensive top-level fallback for older shapes.
    def g(*names, src=None):
        d = src if src is not None else cs
        for n in names:
            if n in d:
                return d[n]
        return None
    compilers = g("Compilers", "compilers")
    if isinstance(compilers, list) and compilers:
        success = all(g("Success", "success", src=c) is True for c in compilers)
        errors = sum(g("Errors", "errors", src=c) or 0 for c in compilers)
        needs = any(g("NeedsBuild", "needsBuild", src=c) for c in compilers)
        if g("IsBuilding", "isBuilding") or any(g("IsBuilding", "isBuilding", src=c) for c in compilers):
            raise VpError("compile in progress — wait for the editor to finish, then rerun.")
    else:
        success = g("Success", "success")
        errors = g("Errors", "errors")
        needs = g("NeedsBuild", "needsBuild")
    if success is False and (errors in (0, None)) and needs is False:
        raise VpError(
            "compile WEDGED (success=False + 0 diagnostics + needsBuild=False): the "
            "editor compiler crashed internally and is running a STALE assembly. "
            "Recover by bumping a source mtime (PowerShell: "
            "(Get-Item Code\\Testing\\VehicleBridge.cs).LastWriteTime = Get-Date), wait "
            "~8 s, re-check compile_status. (gotchas.md Tooling: compiler wedge)")
    if not success:
        raise VpError(f"compile not clean (success={success}, errors={errors}); fix "
                      f"the build before running the battery.")
    if errors:
        raise VpError(f"compile has {errors} error(s); fix the build first.")
    return cs


# ───────────────────────────── the runner ─────────────────────────────

class Run:
    """One maneuver entry -> one verdict."""
    def __init__(self, entry, source):
        self.entry = entry
        self.source = source
        self.maneuver = entry.get("maneuver", "?")
        self.car = entry.get("car", "?")
        self.station = entry.get("station", "?")
        self.passed = None            # None until evaluated
        self.reason = ""              # failure summary
        self.metric_details = []      # per-assert "name=got op val" strings
        self.telem = {}

    def _line(self):
        v = "PASS" if self.passed else "FAIL"
        metrics = " ".join(self.metric_details)
        parts = [f"[vp_test] RUN {self.maneuver} car={self.car} {v}"]
        if metrics:
            parts.append(metrics)
        elif self.passed is False and self.reason:
            parts.append(f"({self.reason})")
        return " ".join(parts)


def _args_json(payload):
    """Project [McpTool]s take ONE string param: {"argsJson": "<json>"} — never the
    object directly (gotchas.md, editor-MCP calling convention)."""
    return {"argsJson": json.dumps(payload)}


def execute_run(url, run):
    """Drive one maneuver live. Sets run.passed / reason / metric_details."""
    entry = run.entry
    # spawn (retry a transient post-transition "vp_ not registered" a few times before
    # classifying it as a real harness-missing failure)
    spawn_args = _args_json({"station": run.station, "car": run.car})
    last_err = None
    spawn_resp = None
    for attempt in range(4):
        try:
            spawn_resp = mcp_json(url, "vp_spawn", spawn_args, timeout=60.0)
            last_err = None
            break
        except VpError as e:
            last_err = e
            msg = str(e).lower()
            if ("not found" in msg or "unknown tool" in msg or "no tool" in msg) and attempt < 3:
                time.sleep(1.0)
                continue
            break
    if last_err is not None:
        run.passed = False
        run.reason = _classify_tool_error("vp_spawn", last_err)
        return
    # FAIL CLOSED on an unresolved station (audit 2026-07-13 HIGH): vp_spawn reports stationResolved;
    # false means the requested station is not in the built world and the pilot would reset in place —
    # the maneuver would then measure against free-drive/wrong geometry. Abort rather than mislabel it
    # as a physics result.
    if isinstance(spawn_resp, dict) and spawn_resp.get("stationResolved") is False:
        run.passed = False
        run.reason = (f"station '{run.station}' did not resolve at spawn (stationResolved=false) — "
                      f"refusing to measure against unresolved geometry (is the proto world built?)")
        return
    # kick off the scripted maneuver
    drive_args = _args_json({"op": "maneuver", "maneuver": run.maneuver,
                             "car": run.car, "station": run.station,
                             "params": entry.get("params", {})})
    try:
        mcp(url, "vp_drive", drive_args, timeout=60.0)
    except VpError as e:
        run.passed = False
        run.reason = _classify_tool_error("vp_drive", e)
        return
    # poll for completion
    deadline = time.time() + POLL_TIMEOUT_S
    telem = {}
    while time.time() < deadline:
        try:
            telem = mcp_json(url, "vp_drive", _args_json({"op": "status"}), timeout=60.0)
        except VpError as e:
            run.passed = False
            run.reason = _classify_tool_error("vp_drive status", e)
            return
        status = str(telem.get("status", "")).lower()
        if status in ("done", "complete", "completed", "finished"):
            break
        if status in ("error", "failed"):
            run.passed = False
            run.reason = f"maneuver reported status={status}: {telem.get('message','')}"
            run.telem = telem
            return
        time.sleep(POLL_INTERVAL_S)
    else:
        run.passed = False
        run.reason = f"timeout after {POLL_TIMEOUT_S:.0f}s waiting for status=done"
        run.telem = telem
        return
    run.telem = telem
    _evaluate(run, telem)


def _classify_tool_error(tool, err):
    msg = str(err).lower()
    if "not found" in msg or "unknown tool" in msg or "no tool" in msg:
        return HARNESS_MISSING
    return f"{tool}: {err}"


def _evaluate(run, telem):
    details, all_ok = [], True
    for a in run.entry.get("asserts", []):
        ok, detail = eval_assert(a, telem)
        details.append(detail if ok else f"{detail} FAIL")
        all_ok = all_ok and ok
    run.metric_details = details
    run.passed = all_ok
    if not all_ok:
        run.reason = "assert(s) failed"


# ───────────────────────────── drivers ─────────────────────────────

def collect_files(opts):
    if opts.all:
        if not os.path.isdir(MANEUVER_DIR):
            raise VpError(f"no maneuver dir at {MANEUVER_DIR}")
        return sorted(os.path.join(MANEUVER_DIR, f)
                      for f in os.listdir(MANEUVER_DIR) if f.endswith(".json"))
    if opts.spec:
        return [opts.spec if os.path.isabs(opts.spec) else os.path.join(ROOT, opts.spec)]
    raise VpError("give a spec file or --all")


def run_dry(files):
    """Offline: validate every spec file. Returns (results, all_ok)."""
    print("[vp_test] DRY RUN -- offline spec validation (no editor needed)\n")
    results, all_ok = [], True
    for path in files:
        rel = os.path.relpath(path, ROOT)
        try:
            entries = load_entries(path)
        except (ValueError, VpError) as e:
            print(f"  INVALID {rel}: {e}")
            results.append({"file": rel, "ok": False, "entries": 0, "problems": [str(e)]})
            all_ok = False
            continue
        file_problems = []
        for i, entry in enumerate(entries):
            where = f"{rel}[{i}]" if len(entries) > 1 else rel
            file_problems += validate_entry(entry, where)
        if file_problems:
            all_ok = False
            print(f"  INVALID {rel} ({len(entries)} entr{'y' if len(entries)==1 else 'ies'}):")
            for p in file_problems:
                print(f"      - {p}")
        else:
            names = ", ".join(f"{e['maneuver']}/{e['car']}@{e['station']} "
                              f"({len(e['asserts'])} asserts)" for e in entries)
            print(f"  OK      {rel}: {names}")
            # advisory station check (warn only)
            for e in entries:
                if e.get("station") not in STATIONS:
                    print(f"            note: station '{e['station']}' not in known "
                          f"registry {sorted(STATIONS)} (advisory)")
        results.append({"file": rel, "ok": not file_problems, "entries": len(entries),
                        "problems": file_problems})
    return results, all_ok


def run_live(url, files):
    """Live battery. Preflight, then drive every entry."""
    st, harness = preflight(url)
    print(f"[vp_test] editor OK: {st.get('Project')} engine={st.get('EngineVersion')} url={url}")
    compile_gate(url)
    print("[vp_test] compile gate: clean")
    if not harness:
        print(f"[vp_test] WARNING: {HARNESS_MISSING}")
    else:
        # fail closed unless we're about to measure in the proto world (audit 2026-07-13 HIGH)
        st = measurement_world_gate(url, files)
        built = " (built)" if st.get("worldBuilt") else " (pending ConVar; per-run stationResolved enforces stations)"
        print(f"[vp_test] measurement world: {st.get('world')}{built}")

    runs = []
    for path in files:
        entries = load_entries(path)
        for entry in entries:
            probs = validate_entry(entry, os.path.relpath(path, ROOT))
            r = Run(entry, path)
            if probs:
                r.passed = False
                r.reason = "; ".join(probs)
                print(r._line())
                runs.append(r)
                continue
            if not harness:
                r.passed = False
                r.reason = HARNESS_MISSING
                print(r._line())
                runs.append(r)
                continue
            try:
                robust_play_start(url)
                execute_run(url, r)
            except VpError as e:
                r.passed = False
                r.reason = f"play/drive error: {e}"
            finally:
                try:
                    mcp(url, "play_stop", timeout=60.0)
                except VpError:
                    pass
                ensure_stopped(url)  # verify the stop landed before the next entry's play_start
            print(r._line())
            runs.append(r)
    return runs


def print_run_table(runs):
    print("\n================= VERDICT TABLE =================")
    print(f"{'maneuver':<12} {'car':<10} {'station':<12} {'result':<6}")
    print("-" * 46)
    for r in runs:
        res = "PASS" if r.passed else "FAIL"
        print(f"{r.maneuver:<12} {r.car:<10} {r.station:<12} {res:<6}")
    n_fail = sum(1 for r in runs if not r.passed)
    print("-" * 46)
    print(f"{'TOTAL':<12} {'':<10} {'':<12} "
          f"{('PASS' if n_fail == 0 else f'FAIL({n_fail})'):<6}")
    return n_fail


def main(argv):
    ap = argparse.ArgumentParser(description="vehicle_prototyping maneuver-battery regression gate")
    ap.add_argument("spec", nargs="?", help="a specs/maneuvers/*.json file (omit with --all)")
    ap.add_argument("--all", action="store_true", help="run every specs/maneuvers/*.json (the gate)")
    ap.add_argument("--dry-run", action="store_true", help="validate spec files offline (no editor)")
    ap.add_argument("--url", default=mcp_client.DEFAULT_URL)
    ap.add_argument("--json", action="store_true", help="print a JSON summary at the end")
    opts = ap.parse_args(argv)

    try:
        files = collect_files(opts)
    except VpError as e:
        print(f"[vp_test] {e}", file=sys.stderr)
        return 2
    if not files:
        print("[vp_test] no spec files found", file=sys.stderr)
        return 2

    if opts.dry_run:
        results, all_ok = run_dry(files)
        n_bad = sum(1 for r in results if not r["ok"])
        print("\n================= DRY-RUN SUMMARY =================")
        print(f"{'file':<40} {'result':<8} {'entries':>7}")
        print("-" * 58)
        for r in results:
            print(f"{r['file']:<40} {('OK' if r['ok'] else 'INVALID'):<8} {r['entries']:>7}")
        print("-" * 58)
        print(f"{len(results)} files, {n_bad} invalid -> {'PASS' if all_ok else 'FAIL'}")
        if opts.json:
            print("\nJSON " + json.dumps(results))
        return 0 if all_ok else 1

    # live
    try:
        runs = run_live(opts.url, files)
    except VpError as e:
        print(f"[vp_test] preflight/gate failed: {e}", file=sys.stderr)
        return 2
    n_fail = print_run_table(runs)
    if opts.json:
        print("\nJSON " + json.dumps([
            {"maneuver": r.maneuver, "car": r.car, "station": r.station,
             "passed": bool(r.passed), "reason": r.reason,
             "metrics": r.metric_details, "telemetry": r.telem}
            for r in runs
        ]))
    return 0 if n_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
