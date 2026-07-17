# Playtest Harness — the working recipe + interface freeze

The complete procedure for driving the live s&box editor from a script to run the
maneuver battery, measure telemetry, and gate a change on the numbers.

**The two halves:**
- **Python side:** `tools/mcp_client.py`, `tools/vp_test.py`, `specs/maneuvers/*.json`.
  `python tools/vp_test.py --dry-run --all` is green with no editor and no C#.
- **C# side:** the `vp_status` / `vp_spawn` / `vp_drive` / `vp_audit` `[McpTool]`s +
  `VehicleBridge` + `VehiclePilot` (under `Editor/` and `Code/Testing/`). If that harness
  assembly is not loaded in the editor, the runner degrades gracefully (see §4). **§6 below is
  the contract the two sides share — treat it as the interface freeze.**

---

## 1. Reaching the editor MCP server

The s&box editor serves MCP over plain HTTP. **The port is per-editor configurable**:
Edit → Preferences (Editor Settings) → **MCP Server** page → "Mcp Server Enabled"
checkbox + "Mcp Server Port" field. When live the page shows
`🟢 Running at http://127.0.0.1:<port>/mcp`. Toggling Enabled off/on restarts the listener.

**Multi-editor note — never talk to another project's editor.** Ports are NOT pinned — the
s&box MCP port preference is one global setting that drifts per editor launch, so any port
table is a stale hint. The discipline is: set `$VP_MCP_URL` (or pass `--url`) to the current
editor's full `/mcp` URL, and identity-probe before any mutating call. Historical hints for
this repo: 7290/7297.

Default endpoint is `$VP_MCP_URL` if set, else `http://127.0.0.1:7290/mcp`; override with `--url`.

**Identity-probe before any mutating call:**

```
python tools/mcp_client.py editor_status --quiet                     # must say "Project":"vehicle_prototyping"
python tools/mcp_client.py search_tools '{"query":"vp_"}' --quiet    # must list vp_drive / vp_audit
```

Only this project's Editor assembly defines the `vp_*` tools, so their presence is proof of
identity beyond the project name. `vp_test.py` does both probes automatically in `preflight()`.

If no editor is running, launch one and poll `editor_status` until it answers (boot + first
compile can take a couple of minutes):

```
"C:\Program Files (x86)\Steam\steamapps\common\sbox\sbox-dev.exe" -project "<path-to>\vehicle_prototyping\vehicle_prototyping.sbproj"
```

Note: during a run the editor window surfacing and the viewport camera jumping around
is **normal harness behavior**.

## 2. The client: `tools/mcp_client.py`

Python stdlib only, pointed at 7290 by default. Auto-handles
the s&box meta-tool layer: `tools/list` exposes only ENTRY-POINT tools (`editor_status`,
`read_console`, `search_tools`, `list_toolsets`, `describe_toolset`, `call_tool`,
`call_tools`); every real tool (`vp_drive`, `compile_status`, `play_start`,
`editor_camera_screenshot`, …) is invoked THROUGH `call_tool`. The client wraps this
automatically — just name the tool. Transport is stateless HTTP POST (JSON-RPC `tools/call`);
no session id, no initialize handshake; it handles both the SSE (`data: {json}`) and plain-JSON
answer forms. `--save-b64 image <file>` rips a screenshot's base64 PNG to disk.

```
python tools/mcp_client.py <tool> ['<json args>'] [--url U] [--raw] [--quiet]
                           [--save-b64 <field> <outfile>]
```

Conventions (from the server's own instructions): vectors/angles are **comma strings**
(`"x,y,z"`, `"pitch,yaw,roll"`), units are inches (1 u = 1 inch, ×39.37 from meters), +z up,
angles in degrees. Tool failures come back as `isError` results, not protocol errors.

## 3. The runner: `tools/vp_test.py`

The regression gate. It loads every `specs/maneuvers/*.json`, and for each entry runs the
loop below, prints a `[vp_test] RUN …` line per run, a verdict table, and exits non-zero on
any failure. `python tools/vp_test.py --all` must be green before landing a change.

```bash
# OFFLINE — validate the spec files (no editor, no C# needed). The CI-safe gate.
python tools/vp_test.py --dry-run --all
python tools/vp_test.py --dry-run specs/maneuvers/launch.json

# LIVE — drive the battery (needs the editor open + the C# harness landed).
python tools/vp_test.py --all
python tools/vp_test.py specs/maneuvers/skidpad.json
python tools/vp_test.py --all --json         # + machine-readable summary
```

**The live loop, per maneuver entry** (compile-gate → play → drive →
read → stop, vehicle-specialized):

```
1. identity-probe   editor_status.Project == "vehicle_prototyping"
                    AND search_tools 'vp_' lists the harness tools.
2. compile gate     compile_status must be Success + Errors=0. REFUSE to run on a
                    stale/wedged compile (§5). This is the truth before play_start.
3. play_start
4. vp_spawn  {"station": S, "car": C}                     seat the car at the station.
5. vp_drive  {"op":"maneuver","maneuver":M,"car":C,        kick off the scripted run.
              "station":S,"params":{...}}
6. vp_drive  {"op":"status"}   (poll every 0.5 s, ≤90 s)   until telemetry.status == "done".
7. evaluate asserts against the returned telemetry JSON.
8. play_stop
```

### Expected output

Dry-run (all specs valid):

```
[vp_test] DRY RUN -- offline spec validation (no editor needed)

  OK      specs\maneuvers\brake.json: brake/hatch@brakezone (4 asserts)
  ...
================= DRY-RUN SUMMARY =================
file                                     result   entries
----------------------------------------------------------
specs\maneuvers\brake.json               OK             1
...
----------------------------------------------------------
7 files, 0 invalid -> PASS
```

Live (once the C# harness lands), one line per run then the table:

```
[vp_test] editor OK: vehicle_prototyping engine=26.07.08e url=http://127.0.0.1:7290/mcp
[vp_test] compile gate: clean
[vp_test] RUN launch car=hatch PASS zeroToHundredS=8.3 <= 12.0 wheelspinS=1.2 <= 3.0 pitchDeg=6.1 <= 15.0 flips=0 == 0 fallThroughs=0 == 0
[vp_test] RUN brake car=hatch PASS brakeDistanceM=40.5 <= 60.0 ...
...
================= VERDICT TABLE =================
maneuver     car        station      result
----------------------------------------------
launch       hatch      dragstrip    PASS
brake        hatch      brakezone    PASS
...
----------------------------------------------
TOTAL                                PASS
```

This is the in-game/console mirror of the `[vp] RUN <maneuver> car=<id> PASS|FAIL`
line: the C# pilot emits the `[vp] RUN` line to the console; `vp_test.py` emits the
`[vp_test] RUN` line from the telemetry it read back over the bridge.

## 4. Spec files: `specs/maneuvers/*.json`

One JSON object per file (a bare list and a `{"suite":[...]}` wrapper are also accepted, so a
file may hold several runs). Schema:

```jsonc
{
  "maneuver": "launch",            // required: a battery maneuver name
  "car":      "hatch",             // required: CarDefinition id
  "station":  "dragstrip",         // required: proving-ground station id
  "params":   { ... },             // optional: maneuver-specific inputs, passed to the pilot
  "asserts": [                     // required (non-empty): pass conditions on telemetry fields
    { "metric": "zeroToHundredS", "op": "<=", "value": 12.0 },
    { "metric": "coneStrikes",    "op": "==", "value": 0 },
    { "metric": "lateralGAvg",    "op": "between", "value": [0.5, 1.3] },
    { "metric": "catchable",      "op": "==", "value": true }
  ]
}
```

- **`op`** ∈ `<=` `>=` `==` `between`. `value` is a number for `<=`/`>=`/`==`, a bool for
  `==`, a `[lo, hi]` pair for `between`. An assert may carry an extra `"note"` (ignored by the
  runner; used here to mark placeholders).
- **`metric`** must be a field of the frozen telemetry contract (§6). An unknown metric is a
  **dry-run error** — this is deliberate: it prevents a spec from drifting away from the C#
  telemetry the runner actually consumes.
- A **missing** metric at live-run time (the telemetry didn't include an asserted field) is a
  **run failure**, not a skip — the contract wasn't met.

**Shipped battery:** `launch`, `topspeed`, `brake`, `skidpad`, `slalom`,
`jturn`, `jump`, the wave-2 four (`liftoff`, `washboard`, `hillclimb`, `figure8`, added
2026-07-13), `driftexit` (added 2026-07-13 — drift-exit recovery on the two
oversteer-prone cars), and `spinrecovery` (added 2026-07-15 — backward-slide kill after a handbrake
spin, hatch + coupe): **13 spec files / 13 live maneuvers**. Full crash/destruction simulation is
out of scope for this kit; `crash` is not a live maneuver here, its spec is removed, and the
runner **rejects** any reintroduced crash spec at dry-run with a clear out-of-scope message.
The original seven's assert values were tightened through successive rounds
(see `docs/handling-targets.md` + `docs/baseline-metrics.md`); the wave-2 four carry v1
pre-measurement bands from handling-targets' provenance tables (first measured values in
baseline-metrics' "Wave-2 battery extension" section — several bands are honest reds pending
re-anchor).

**Wave-2 profiles LANDED (2026-07-13):** `liftoff`, `washboard`, `hillclimb`, `figure8` are
live maneuvers (`Code/Testing/Maneuvers/{Liftoff,Washboard,HillClimb,Figure8}Maneuver.cs` +
registry entries), driven per the §8 recipe. The two open items noted at spec-authoring time
are both resolved: the hillclimb station now reaches 45% (9-ramp parallel fan — see
`proving-grounds.md`), and `figure8.json` carries the full 3-assist-level sweep (12 rows,
4 cars x Casual/Sport/Sim). One metric correction landed with the profiles: the washboard
specs assert `wheelContactLossPct` (per-wheel contact loss — the metric the bands were
authored against, per handling-targets feel-heuristic 3) instead of the full-airborne
`contactLossPct`, which measured 0.0 on every car live (raycast wheels skipping 0.12 m ridges
rarely put the whole car airborne). Band values unchanged.

**Station ids** the runner recognizes (advisory — unknown stations warn, don't fail; the
authoritative registry is authored in C#): `dragstrip`, `brakezone`, `skidpad`,
`slalom`, `ramps`, `openpad`, `washboard`, `hillgrade`, `bankedcurve`, `lowgrip`, `city`.
`crashwall`/`crashwall_reserved` remain a **reference-only reserved plot** — TestTrack still
builds it, but the `crash` maneuver that consumed it is out of scope for this kit, so no
spec here spawns there.

**Fail-closed measurement world (2026-07-13):** the live runner refuses to
drive unless `vp_status.world == "proto"` (a persisted `vp_world playground` leaves the proto
TestTrack unbuilt), and every `vp_spawn` reply's `stationResolved` is checked — a `false`
aborts that run rather than silently measuring against free-drive geometry. `vp_status` now
carries `world` + a proving-ground station census for the preflight to read.

### Degrading gracefully before the C# harness exists

- **`--dry-run`** never touches the editor: it fully validates the spec files (schema, ops,
  metric names vs the contract) and is the gate a pre-C# runner / CI runs.
- **Live mode** with the harness absent: `preflight()` finds no `vp_` tools via `search_tools`
  and every run FAILs with the exact message *"harness C# not landed — vp_ [McpTool]s
  (vp_status/vp_spawn/vp_drive/vp_audit) are not registered in this editor…"*. A `vp_` call
  that returns tool-not-found is classified the same way. No cryptic tracebacks.

## 5. Troubleshooting

- **Endpoint refuses connection** — MCP disabled or wrong port. Check the Editor Settings →
  MCP Server page (you may need to toggle Enabled off/on); confirm the port is **7290**.
  `netstat -ano | findstr 7290` shows the listener.
- **Editor has another project loaded** — `editor_status` says so; `vp_test.py` aborts with
  "WRONG PORT". Do NOT proceed; that port belongs to another project's editor.
- **`harness C# not landed`** — expected until the vp_ McpTools are compiled into the editor. Use `--dry-run`.
- **Stale assembly** — editor opened before recent commits runs old code. ALWAYS gate on the
  editor's `compile_status`, not `dotnet build` (whose incremental warning/error counts lie).
  Bump a source mtime to force a fresh compile:
  `(Get-Item Code\Testing\VehicleBridge.cs).LastWriteTime = Get-Date`, wait ~8 s, re-check.
- **Compiler WEDGE** — `compile_status` reads `Success=false` **with 0 diagnostics and
  `NeedsBuild=false`**: the in-editor compiler crashed internally and silently keeps running
  the stale assembly forever; no code error exists to fix. `vp_test.py`'s `compile_gate()`
  detects this exact signature and refuses to run with the recovery hint. Fix: bump any watched
  source file's mtime (as above) to dirty the compile; it rebuilds clean.
- **Bridge carries stale state across Play→Stop→Play** — the `VehicleBridge` static facade
  MUST session-reset (clear per-session flags + drop any un-consumed command) in the boot
  singleton's `OnEnabled`, or the first `vp_drive` of the new session no-ops against last
  session's leftover state (a leftover flag can make the director
  skip spawning). If a live run's telemetry looks like the *previous* run's, suspect a missing
  session-reset. Also: **no `System.Threading`** (`Volatile`/`Interlocked`) in the game
  assembly — a plain `int` token compare is whitelist-safe and torn-read-proof on x64; use that
  for the bridge command token.
- **A live run times out at status-poll** — the pilot never reported `status=done`. Check the
  console (`read_console`) for `[vp]` errors; confirm `vp_drive {"op":"status"}` returns a JSON
  object with a `status` field.

## 6. The C# contract (INTERFACE FREEZE)

`tools/vp_test.py` and `tools/mcp_client.py` are written against exactly this surface. The C#
harness (`Editor/VpTools.cs`, `Code/Testing/VehicleBridge.cs`,
`Code/Testing/VehiclePilot.cs`) must satisfy it. **Changing a field name here means changing
the runner — keep them in lockstep** (the `KNOWN_METRICS` set in `vp_test.py` is the machine
copy of §6.2).

### 6.1 `[McpTool]` signatures

All tools take a single JSON-string argument (the s&box `call_tool` convention) and return a
JSON string in the result's text content block (single-encoded).

| Tool | Args | Returns |
|---|---|---|
| `vp_status()` | none / `{}` | read-only status JSON: `{ project, scene, compileOk, isPlaying, activeCar, bridgeState, harnessVersion }` |
| `vp_spawn(argsJson)` | `{ "station": <id>, "car": <id> }` (omit both = reset in place) | `{ ok, car, station, seatZ }` — seats at suspension equilibrium (NOT surface+radius) |
| `vp_drive(argsJson)` | `{ "op": <op>, ... }`, `op ∈ {status, spawn, maneuver, route, stop, reset}` | for `op:"maneuver"` → ack `{ ok, runId, status:"running" }`; for `op:"status"` → the **telemetry report** (§6.2); POST-and-poll via the bridge |
| `vp_audit()` | none / `{}` | re-runs invariant audits on demand; returns/logs the greppable `[vp] AUDIT <name> offenders=N target 0` lines |

`vp_drive` op detail: `maneuver` runs a named scripted test with `{maneuver, car, station,
params}`; `route` drives waypoint lists; `stop`/`reset` clear the run; `status` returns live +
last-completed-run telemetry. The runner uses `maneuver` (kick-off) then polls `status`.

### 6.2 Telemetry report — the exact JSON fields the runner consumes (FROZEN)

The object `vp_drive {"op":"status"}` returns. Envelope fields drive the poll loop; the rest
are the metrics specs assert on. Numbers are SI (meters, m/s, seconds, g, degrees, deg/s).

**Envelope (run control):**

| field | type | meaning |
|---|---|---|
| `status` | string | `idle` \| `running` \| `done` \| `error` (runner polls until `done`; `error` fails the run) |
| `maneuver` | string | echo of the running/last maneuver |
| `car` | string | echo of the car id |
| `station` | string | echo of the station id |
| `runId` | int | monotonic run counter (bridge token; plain int, no System.Threading) |
| `message` | string | human note, esp. on `status:"error"` |

**Metrics (assertable; every field name below is in `KNOWN_METRICS`):**

| field | type | unit | maneuvers that read it |
|---|---|---|---|
| `speedMs` | float | m/s | (final/instant speed) all |
| `maxSpeedMs` | float | m/s | topspeed |
| `distanceM` | float | m | brake, launch, all |
| `elapsedS` | float | s | slalom, all |
| `lateralGPeak` | float | g | skidpad, liftoff, figure8 |
| `lateralGAvg` | float | g | skidpad, figure8 |
| `longGPeak` | float | g | launch, brake |
| `longGAvg` | float | g | launch, brake |
| `yawRatePeakDeg` | float | deg/s | slalom, jturn |
| `yawRateAvgDeg` | float | deg/s | skidpad, figure8 |
| `pitchDeg` | float | deg | launch (peak body pitch) |
| `rollDeg` | float | deg | skidpad, jump (peak body roll) |
| `headingDriftDeg` | float | deg | brake (heading change while braking) |
| `gear` | int | — | all |
| `gearAtVmax` | int | — | topspeed |
| `rpm` | float | — | all |
| `wheelspinS` | float | s | launch, hillclimb |
| `zeroToHundredS` | float | s | launch (0→100 km/h = 0→27.78 m/s) |
| `brakeDistanceM` | float | m | brake (100→0 km/h distance) |
| `lockupTicks` | int | ticks | brake |
| `airtimeS` | float | s | jump |
| `landingPitchDeg` | float | deg | jump |
| `landingRollDeg` | float | deg | jump |
| `settleS` | float | s | jump, washboard (suspension settle after landing; stays 0 if the car never goes fully airborne) |
| `jturnTimeS` | float | s | jturn (handbrake 180°) |
| `yawOvershootDeg` | float | deg | jturn (degrees past target 180°); liftoff (peak yaw deviation from the lift-instant heading — the disturbance reading of the same field, re-baselined at lift) |
| `coneStrikes` | int | count | slalom |
| `contactLossPct` | float | % | jump (fraction of POST-FIRST-CONTACT ticks with ALL wheels off the ground — full-airborne) |
| `wheelContactLossPct` | float | % | washboard (average % of per-wheel contact lost — the handling-targets feel-heuristic-3 metric; added 2026-07-13 wave-2 after full-airborne contactLossPct measured 0.0 on every car over the ridges) |
| `rollbackM` | float | m | hillclimb (max backward slide from the furthest-up point, climb phase only); spinrecovery (furthest travel in the old travel direction after the spin, from handbrake release) |
| `recoveryS` | float | s | spinrecovery (throttle-commit-after-spin → forwardSpeed > +0.5 m/s; reports the full un-recovered duration on a maxRunS DNF, never a false 0 — added 2026-07-15) |
| `exitRecoveryS` | float | s | driftexit (handbrake release → \|rear slip angle\| < 8°; reports the full un-recovered duration on a maxRunS DNF, never a false 0 — added 2026-07-13) |
| `speedRetention` | float | ratio | driftexit (exit speed / entry speed across the slide — the "lose too much momentum" metric) |
| `peakSlipDeg` | float | deg | driftexit (deepest \|rear slip angle\| across the slide — the "how deep did it get" diagnostic) |

**`contactLossPct` measurement window (2026-07-13):** both the numerator (ticks
with all wheels airborne) AND the denominator (total ticks) start at the FIRST real ground
contact, so the pre-ground spawn/motion-freeze interval no longer dilutes the fraction. Before
this fix the numerator was already post-contact but the denominator was whole-run, which
systematically diluted the value on short runs. `wheelContactLossPct` already used a matching
post-first-contact denominator (`_wheelTicks`); the two now agree on their window. No shipped
spec asserts `contactLossPct` (washboard asserts `wheelContactLossPct`), so this is a
telemetry-fidelity fix with no verdict change.

**Standing invariant audits — every run reports these, target 0:**

| field | type | meaning |
|---|---|---|
| `flips` | int | full roll/pitch-over events |
| `flippedTicks` | int | ticks spent inverted |
| `fallThroughs` | int | fell through collision (off the world edge) |
| `stuckTicks` | int | ticks stuck (no progress under throttle) |
| `nanForces` | int | NaN in the force solver |
| `sleepWhileDriving` | int | Rigidbody slept while under drive |

**Booleans (asserted with `op:"=="`):**

| field | type | meaning |
|---|---|---|
| `catchable` | bool | jturn: yaw settled without spinning past 220° |
| `spunOut` | bool | skidpad, figure8, liftoff: lost the rear / spun (yaw-rate runaway) |
| `climbed` | bool | hillclimb: held the rated grade to ~75% up its ladder ramp (crest threshold — the run ends on the slope, before the ramp's elevated top edge) |

The C# `TelemetryReport` should serialize with **exactly these camelCase names**. New metrics
are added by appending here AND to `KNOWN_METRICS` in `tools/vp_test.py` in the same change
(the two are the interface; drift between them is the failure mode this doc exists to prevent).

## 7. Amendments — per-maneuver assist policy, launch split, run-status semantics

*(Added 2026-07-12.)*

### 7.1 Per-maneuver assist policy (`params.assist`)

A spec entry may pin the car's assist level for that run: `"assist": 0` (Casual), `1` (Sport),
`2` (Sim). The pilot re-applies the pin every tick of the run (the freshly spawned car's
`OnStart` would otherwise reset `Assists = DefaultAssists` after the maneuver starts) and the
car reverts to its `DefaultAssists` on the next run without the param.

**Standing policy — J-turn runs pin `assist: 1` (Sport) for RWD cars** (coupe, kart, pickup):
the handbrake J-turn *requests* yaw, and Casual's stability damper — the exact tame that keeps
these cars catchable in normal driving — re-engages the instant the RWD profile releases the
handbrake for its power-slide and kills the deliberate rotation (measured finding: coupe never
completed a 180). The FWD hatch keeps Casual: its rotation phase holds the handbrake
throughout, which already bypasses the stability damp, and its catchability band was authored
against the Casual settle. Rationale: assists are a *player-facing* layer; a deliberate
aggressive maneuver is tested at the assist level a player attempting it would choose.

### 7.2 Launch split speed (`params.splitSpeedMs`)

`zeroToHundredS` records the time to reach `splitSpeedMs` (default 27.78 m/s = 100 km/h). The
kart's launch spec sets `13.89` (50 km/h) because its top speed (55–70 km/h band) is below
100 km/h — the original 0–100 band was self-inconsistent (revised, see
`docs/handling-targets.md` Kart launch row). The field NAME stays `zeroToHundredS` (frozen
contract §6.2); the kart's spec note carries the real meaning.

### 7.3 `status:"error"` vs `status:"done"` (audit 2026-07-12)

Operational failures — no active car, car went invalid mid-run, unknown maneuver — now report
`status:"error"` (the runner fails the run immediately with the message). `status:"done"`
means *the telemetry is a real measurement*. A `maxRunS` **timeout stays `done`** with the
`maxRunS reached` message: its metrics (coneStrikes, elapsedS, stuckTicks…) are genuine
measured data of a car that did not finish, and specs assert `elapsedS` to catch the DNF.

### 7.4 In-game verdict card is "invariants only"

`UiFeed.LastRunPass` checks the standing invariant audits (flips, fallThroughs, nanForces,
stuckTicks, sleepWhileDriving) plus a clean message — NOT the handling-target bands. The card
title carries an explicit "invariants only" suffix. The authoritative band verdict is
`vp_test.py`'s.

**Later update:** the shared verdict DTO now exists — `RunVerdict` (`Code/Testing/RunVerdict.cs`).
The console `[vp] RUN … done …` line and the in-game card are both projected from it (one metric
definition per maneuver instead of the old parallel `EmitRunLine` / `BuildMetricRows` switches), and
the card now shows the invariant audits as REAL per-assert rows (each `flips`/`fallThroughs`/… with
its own pass/fail) rather than a single aggregate bool. It is still "invariants only": handling-target
bands stay CLI-side (`LastRunOutOfBand` remains false) because the JSON specs live outside the game
assembly. `vp_test.py`'s `KNOWN_METRICS` and the frozen §6.2 contract are unchanged by the refactor.

## 8. Adding a maneuver (decomposed layout)

The `VehiclePilot` was originally one ~1000-line component (command intake + spawning + every maneuver
state machine + telemetry + audits + output + UI projection). It is now decomposed
so each concern is its own file under `Code/Testing/` and **adding a
maneuver is local** — no pilot edit:

| Concern | File |
|---|---|
| Command consumption + dispatch (thin) | `Code/Testing/VehiclePilot.cs` |
| Station/car resolution (shared w/ VpTools, CarSwitcher, part-kit cmds) | `Code/Testing/StationCarRegistry.cs` |
| Per-tick telemetry contract accumulation | `Code/Testing/TelemetryAccumulator.cs` |
| Standing invariant audits | `Code/Testing/InvariantAuditAccumulator.cs` |
| Shared run verdict DTO (console line + UI card) | `Code/Testing/RunVerdict.cs` |
| One scripted maneuver each | `Code/Testing/Maneuvers/<Name>Maneuver.cs` |
| Maneuver interface + shared plateau helper | `Code/Testing/Maneuvers/IManeuver.cs` |
| Name → maneuver table | `Code/Testing/Maneuvers/ManeuverRegistry.cs` |

**To add a maneuver `foo`:**

1. **New class** `Code/Testing/Maneuvers/FooManeuver.cs : ManeuverBase` (or `: IManeuver`).
   - `Name => "foo"` (must match the spec's `maneuver` field).
   - `Start(ctx)` — reset this maneuver's own per-run phase fields.
   - `Tick(ctx, dt)` — stage inputs via `ctx.Drive(fwd, steer, handbrake)` (NEVER apply forces
     directly); read shared telemetry via `ctx.Telemetry` (`YawAccumDeg`, `ContactlessS`, `Landed`,
     `StartFwd`, …) and `VehicleBridge.*`; return `true` when the maneuver's OWN completion condition
     is met (else it ends on `maxRunS`). Re-baseline yaw at a phase change with
     `ctx.Telemetry.ResetYawTracking(ctx.Car, resetMax)`.
   - `Report(verdict)` — `verdict.Add(logToken, label, value)` once per metric; this single definition
     feeds both the console line and the card row. Set `verdict.YawSummary` if the maneuver has a
     yaw-stability readout. Optionally override `TimingValue(ctx)` for the live top-center widget.
2. **Registry entry** — one line in `ManeuverRegistry._byName`: `["foo"] = new FooManeuver(),`.
3. **Spec file** — `specs/maneuvers/foo.json` (§4 schema). Add `foo` to `MANEUVERS` in `vp_test.py`
   if it is a new battery maneuver name, and any NEW telemetry field to BOTH §6.2 here and
   `KNOWN_METRICS` in `vp_test.py` (the frozen-contract lockstep rule).
4. If `foo` needs a station the pilot's ConVar on-ramp should default to, add it to
   `StationCarRegistry.DefaultStationFor`.

An unknown maneuver name (spec present, no class/registry entry) reports `status:"error"`
("unknown maneuver 'foo'"), exactly as before — the `washboard`/`hillclimb`/`liftoff`/`figure8`
specs sit in that state until their classes land.
