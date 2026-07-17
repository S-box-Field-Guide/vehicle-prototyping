# Part-kit vehicle assembly (Stage A)

Status: **live-verified 2026-07-11** (Stage-A assembly).
Contract: `docs/part-kit-pipeline.md` + `Assets/models/vehicles/hatch_kit/manifest.json`

The `VehicleFactory` part-assembly path: HatchKit builds from 11 separate part GameObjects
(chassis shell, 4 wheels, 2 doors, hood, trunk, 2 bumpers) instead of a fused mesh, with wheel
meshes mirroring the LIVE suspension simulation. The raycast-wheel physics core is unchanged.

> **Update 2026-07-15:** the fused-body fallback and the off-roster
> `hatchfused` equivalence twin were RETIRED. A kit that fails to assemble now falls back to a
> primitive **engine blockout** (box/kart body + sphere wheels) with a clear `[vp]` **error** log —
> never a fused stand-in, never a load of a removed asset. Sections below that say "fused/blockout"
> fallback or compare the kit against the fused twin describe the pre-retirement design and its
> one-time A/B measurements; the live fallback target today is the blockout.

Files: `Code/Vehicle/Parts/PartKitManifest.cs` (DTOs, loader, frame math),
`Code/Vehicle/Parts/PartKitAssembler.cs` (assembly), `Code/Vehicle/Parts/PartKitCommands.cs`
(`vp_spawn_kit` console on-ramp), plus minimal edits to `VehicleFactory.cs` (two additive
branches) and `CarDefinition.cs` (`PartKitManifest` field + `HatchKit` roster entry).

---

## 1. Architecture

```
root (Rigidbody, VehicleController, def-driven BoxCollider)   ← physics frame, +X fwd (UNCHANGED)
├── "Kit Body"  LocalRotation = FromYaw(-90), z = -SeatHeightM(def)   ← the ONE frame fix
│   ├── Part chassis_shell   ModelRenderer + BoxCollider(dims_m)   ┐ rigid to body
│   ├── Part door_l/door_r   ModelRenderer + BoxCollider(dims_m)   │ (Stage C makes the
│   ├── Part hood / trunk    ModelRenderer + BoxCollider(dims_m)   │  hinged ones breakable)
│   └── Part bumper_f/_r     ModelRenderer + BoxCollider(dims_m)   ┘
├── Wheel FL..RR (VehicleWheel — raycast sim, NO collider, steer yaw set by controller)
│   └── "Visual"  (WheelVisual, base = identity: bob + spin)
│       └── "Mesh" (wheel.vmdl, static FromYaw(±90) axle alignment only)
```

- Part pivots land at `AuthorToLocal(attach_author_m) × 39.37` under the kit-body GO.
- Every part gets a code-side `BoxCollider` sized from `dims_m`, centred on the (corrected,
  §2) bounds centre — the kit vmdls are deliberately render-only (zero engine collision).
- Wheels have **no colliders** — wheel physics stays the raycast sim (locked).
- Any manifest/model failure logs a `[vp]` error and falls back to the primitive blockout
  path (box/kart body + sphere wheels); a broken kit can never brick a spawn or load a removed
  asset (fused fallback retired 2026-07-15).

## 2. Frame resolution — the manifest was wrong, here is the proven truth

The manifest ships `attach_local_m` + `frames.author_to_local = (-bY,-bX,bZ)`. That mapping
has **determinant −1** — a mirror, which a proper-rotation export/import pipeline cannot
produce — so it was treated as suspect and resolved empirically:

| link | mapping | evidence |
|---|---|---|
| Blender export (`forward_axis='NEGATIVE_Z', up_axis='Y'`) | `o = (bX, bZ, −bY)` | disk: `door_l.obj` handle (author Y **+0.038**, the only Y-asymmetric feature) lands at file Z **−0.038** |
| s&box OBJ import | `m = (oZ, oX, oY)` | live: facing screenshots (below). Plain Y-up→Z-up **cyclic permutation, no sign flips** — NOT the negated `(-oZ,-oX,oY)` an earlier convention assumed |
| **net author → model-local** | **`m = (−bY, +bX, bZ)`** | both combined |

**True chassis model-local frame: +X = vehicle RIGHT, +Y = vehicle FRONT (nose points local
+Y), +Z = up. Facing yaw = FromYaw(−90)** to aim the nose down root +X — not the +90° the
pipeline doc predicted.

Manifest fields, verdict:

- `attach_author_m` — **trustworthy** (authoring truth); this is what the code consumes,
  through `PartKitManifest.AuthorToLocal`.
- `attach_local_m` — fore-aft sign flipped; **ignored** (bound in the DTO for reference only).
- `local_bounds_min/max_m` — exactly **180°-yawed** from truth (negate x AND y);
  `PartKitPart.BoundsCenterM` applies the correction for collider centres. `dims_m` are
  size-only and unaffected.
- `frames.chassis_local` ("nose points local −Y; +90° yaw") — wrong on both counts.

The instructive failure (screenshot pair in `screenshots/m4/`): the first landing used the
older negated import formula (`AuthorToLocal=(bY,−bX,bZ)` + yaw +90). Because that is exactly
180° off the truth in BOTH the conversion and the yaw, **every part's pivot POSITION came out
world-correct while every MESH sat 180°-spun in place** — taillights facing forward, doors
swung to cover the front quarters, hatch lid over the roof. The two errors cancel in position
and add in orientation. Consequence baked into the code: the `AuthorToLocal` constants and
`ModelToRootYaw` **must always flip together**, and position-correctness alone proves nothing
about frame-correctness.

**RESOLVED (2026-07-12):** `gen_vehicle.py`'s emission is now fixed to the proven
mapping under schema **`vp.partkit/2`** (pickup_kit was the first v2 kit); the loader
normalizes v1 manifests at load, so `BoundsCenterM` is a plain centre on both schemas and
`attach_author_m` remains the position source everywhere. Details: `docs/pickup-kit.md` §3.
**SUPERSEDED (current state 2026-07-13):** every shipped kit (hatch_kit, coupe_kit, kart_kit,
pickup_kit) now ships schema **`vp.partkit/3`** (adds the D1 destruction damage band) — the
hatch manifest is no longer a v1 artifact. The loader still
accepts v1/v2 with the same normalization, so the frame history above stays true as history.

## 3. Manifest parsing — game-assembly JSON (verified)

`FileSystem.Mounted.ReadAllText("models/vehicles/hatch_kit/manifest.json")` +
`Json.Deserialize<T>` **work in the game assembly, live-verified** (7/7 parts parsed and
built in play mode). Design choices:

- DTO property names are byte-for-byte the JSON snake_case keys — zero serializer
  attributes, minimal whitelist surface (System.Text.Json matches case-sensitively and
  ignores unbound fields like `spec_m`/`frames`).
- `Json.Serialize/Deserialize` + `FileSystem` are already whitelist-proven house APIs
  for save/load; the new datum is that **loose
  `.json` under `Assets/` is readable via `FileSystem.Mounted` in-editor**. Whether loose
  json ships in a packaged build is a packaging question — flagged, not blocking
  (Stage A is a dev-time feature).

## 4. Wheel visual mirroring — reuse WheelVisual, don't parallel it

Decision: **`WheelVisual` is reused unchanged**; no `PartWheelVisual` was written. The trick
is hierarchy, not code: the animated channels live on an identity-based "Visual" GO under the
physics wheel GO, and the kit-specific axle alignment is a static child below it.

| channel | mechanism | status |
|---|---|---|
| steer | `VehicleController.ApplySteering` writes `FromYaw(SteerAngle)` onto the wheel GO; everything below inherits | pre-existing, unchanged |
| bob (compress/rebound) | `WheelVisual.OnUpdate`: `LocalPosition = Down × SuspensionLength × 39.37` | live-verified numerically: at rest the Visual GO sits **3.494 u = 0.0888 m** below the attach = `travel − Mg/4k` = 0.18 − 0.0912 m, hub at 0.301 m ≈ wheel radius. Exact to the mm. |
| spin | `WheelVisual.OnUpdate`: `LocalRotation = base × FromPitch(−spinDeg)`; base = identity ⇒ spin axis is the wheel GO's own Y = the axle, identical world spin direction on both sides | proven component; sign convention inherited from the fused path |
| axle alignment | "Mesh" child, static `FromYaw(+90)` (left) / `FromYaw(−90)` (right, `mirror` flag) puts the manifest's local-X axle on the parent Y, hub face outboard | assembled + screenshot-verified |

Why not consume `rotation_axis_local` dynamically? Stage A has exactly one wheel frame
(axle = local X, manifest contract) — a static ±90° alignment child is simpler and keeps
`WheelVisual` byte-identical for the fused path. If a future kit changes wheel frames, the
alignment child is the single place to generalise.

Side note: the fused path's right-side wheels compose spin under a yaw-180 base, which
reverses their apparent spin direction — invisible only because those meshes are rotationally
symmetric. The kit hierarchy spins both sides world-correctly.

Squash/tire-deform: out of scope for Stage A, and the seam for it later is the Mesh child's
scale.

## 5. Collider & mass layout

- **Root def-driven collider unchanged** (BodySize/RideHeight/GroundClearance box) — it is
  the suspension bump-stop and its parity with the fused twin is part of physics equivalence.
  `HatchKit` deliberately keeps Hatch's `BodySize` for this reason.
- **Part BoxColliders** (chassis + bumpers + doors/hood/trunk, all rigid-to-body via
  compound-to-root): sized from `dims_m`, centred on corrected bounds centres. These give the
  kit car mesh-accurate crash extremities (bumpers ARE the outermost contact at ±1.85 m).
  Known, accepted deltas vs the twin: (a) wall/obstacle contact geometry differs — that is
  the Stage-A feature, not a regression; (b) compound shapes shift the auto-computed inertia
  tensor somewhat (total mass still `MassOverride`). Flat-ground driving is unaffected (launch
  telemetry §6 confirms); the full battery comparison at the Stage-A exit will quantify the rest.
- **Mass fractions: deliberately unused in Stage A** (single rigidbody). They become real at
  Stage C when detached parts get `mass_fraction × def.Mass` rigidbodies.
- Chassis-shell collider belly sits lower than the root collider (0.15 m authored clearance)
  but can never touch flat ground even at full bottom-out (belly 0.569 m below root centre vs
  ground at 0.63 m at zero suspension length) — checked analytically, and the launch run shows
  no contact anomalies.

## 6. HatchKit roster entry & first telemetry comparison

`CarDefinitions.HatchKit` **clones Hatch programmatically** (starts from the `Hatch` instance,
overrides only kit fields) so tuning can never drift from the twin. Geometry follows the kit
mesh — wheels must sit in the authored arches: wheelbase 2.5→**2.55 m**, track 1.52→**1.50 m**,
wheel radius 0.31→**0.30 m**. Everything else (mass, curves, rates, drivetrain, brakes,
steering, arcade dials, BodySize) is Hatch's, live.

Launch maneuver, dragstrip, same run protocol (fused via `vp_spawn`+`vp_drive`; kit via
`vp_spawn_kit`+`vp_drive` maneuver-without-station), 2026-07-11:

| metric | Hatch (fused) | HatchKit (parts) | delta |
|---|---|---|---|
| zeroToHundredS | 10.04 | 9.96 | **−0.8%** |
| maxSpeedMs (in run) | 27.79 | 27.81 | +0.1% |
| wheelspinS | 4.14 | 4.00 | −3.4% |
| pitchDeg (peak) | 0.63 | 0.61 | — |
| distanceM | 145.4 | 144.3 | −0.8% |
| flips / fallThroughs / nanForces / sleepWhileDriving | 0/0/0/0 | 0/0/0/0 | equal |
| stuckTicks | 6 | 6 | equal (spawn settle-freeze) |
| `AUDIT partkit_bounds` | n/a | **offenders=0** | vmdl scale verified |

The −0.8% 0→100 and lower wheelspin match the predicted effective-gearing change from
r 0.31→0.30 (~3% shorter) — a real geometric difference of the kit, not an assembly artifact.
**Full battery equivalence-within-tolerance is the Stage-A exit gate; this is the first comparison
and it is comfortably tight.**

Harness rough edge: after a maneuver reports `done` the pilot nulls
`InputOverride` and the car coasts — off the 400 m strip and eventually off the finite ground
plane (z < −5 m, AFTER telemetry accumulation stopped; both twins behave identically). A
post-run brake phase in `VehiclePilot` would tidy this (the pilot file is part of the input layer).

## 7. Spawn seam

`VehiclePilot.ResolveCar`'s switch only knows hatch/kart/coupe, so the kit spawns through a
console command in a separate file:

```
vp_spawn_kit [station]        # default dragstrip; unknown station → origin (city)
vp_drive {"op":"maneuver","maneuver":"launch"}     # NO station → runs on the active car
```

`vp_spawn_kit` mirrors `SpawnCarAt` (same station registry via public
`VehiclePilot.ResolveStation`, same `SeatHeightM` seating), hands the controller to the pilot
through the public `ActiveCar` property, sets `VehicleBridge.SpawnedCar = "hatchkit"`, and
rebinds the chase camera. `StartManeuver` rebinds its rigidbody from `ActiveCar` at run
start, so maneuvers Just Work. **HUD hook:** the vehicle picker (#5) can list `HatchKit`
by calling `CarDefinitions.HatchKit`; spawning through the pilot additionally needs
`ResolveCar` to learn "hatchkit" (one switch arm).

## 8. Verification status

| check | how | status |
|---|---|---|
| game + editor assemblies build | `dotnet build` both csproj | ✅ green (only pre-existing warnings) |
| manifest parses in game assembly | live spawn, `[vp] partkit ... 7/7 parts` | ✅ |
| kit vmdls compile + materials resolve | live `Model.Load`, no errors, colours render | ✅ (screenshots) |
| parts at right offsets | `get_game_object` on all 7 part pivots vs expected world positions | ✅ exact |
| facing (+X nose) | v1/v2 head-on screenshot pair + hood-low(0.88 m)/trunk-high(1.30 m) numeric probe | ✅ (yaw is **−90°**, doc corrected) |
| bounds vs manifest dims | in-code audit `AUDIT partkit_bounds` | ✅ offenders=0 |
| wheel bob mirrors live sim | Visual GO transform = attach − SuspensionLength, mm-exact | ✅ |
| wheel steer/spin | inherited controller yaw + proven WheelVisual spin path | ✅ architectural + visual (no dedicated motion capture) |
| launch telemetry vs fused twin | table §6 | ✅ first comparison, tight |
| fused path unchanged | zero edits to fused branches; fused Hatch baseline run healthy | ✅ |
| **full battery equivalence** | `vp_test.py --all` style comparison across all maneuvers | ⛔ **Stage-A exit gate** |
| doors/hood/trunk hinge MOTION | swing about manifest axes | ⛔ not exercised (Stage A mounts them rigid; axes recorded) |
| packaged-build loose-json read | shipped build | ⛔ packaging question |

Screenshots (`screenshots/m4/`): `m4_kit_at_dragstrip_editor.png`, `m4_kit_front34.png`,
`m4_kit_rear34.png`, `m4_kit_side_profile.png`, `m4_kit_game_chase.png`,
`m4_kit_in_city_chase.png`, plus the frame-bug evidence pair
`m4_frame_bug_v1_taillights_at_plusx.png` / `m4_frame_fix_v2_nose_at_plusx.png`.

Ready-to-run recheck (editor MCP on 7290):
```
python tools/mcp_client.py play_start --quiet
python tools/mcp_client.py console_command '{"command":"vp_spawn_kit dragstrip"}'
python tools/mcp_client.py vp_drive '{"argsJson":"{\"op\":\"maneuver\",\"maneuver\":\"launch\",\"car\":\"hatchkit\"}"}'
python tools/mcp_client.py vp_drive '{"argsJson":"{\"op\":\"status\"}"}'   # poll to done
python tools/mcp_client.py play_stop --quiet
```

## 9. Stage-C seams (where joint-breaks attach)

- Every hinged/bolt-on part is ONE GameObject whose **pivot already sits at its joint**
  (hinge line / mount plane) with its own collider. Detach = reparent to scene + add
  Rigidbody(`mass_fraction × def.Mass`) + optionally a hinge joint at the current transform.
  Nothing else needs to move.
- Hinge axes are part-local per manifest: doors Z (`open_sign` ±1), hood X (opens
  up-forward), trunk X (up-back). Bumpers rigid, `mount_normal` gives the break-off
  direction. (Manifest axis LETTERS are trustworthy; only signs of the frame doc were wrong —
  swing signs must be verified visually when Stage C animates them, per the frame lesson.)
- Impulse sourcing: part colliders are compound shapes on the root body, so per-part impact
  attribution will need contact-point → nearest-part mapping or per-part `Collider` touch
  callbacks — spike this early in Stage C.
- Wheel loss (drag-on-hub): disable the wheel's raycast + drop `WheelVisual`, physics core
  untouched.

## 10. Known cosmetic quirks (generator authoring, out of assembly scope)

- `build_door` ignores its `side` param: door_r reuses door_l geometry, so the right door's
  2.8 cm handle nub faces inboard. Sub-cm visual, fix belongs in `gen_vehicle.py`.
- The hatch lid + louver slats read as slightly "floating" over the rear tray at some angles
  — authored style, positions verified correct.
- Red lamp material is used on BOTH fascias (no distinct headlight colour), which cost a
  red-herring during facing verification — a `light_front` palette entry would help
  future eyeball checks.

## 11. Hardening pass (2026-07-12)

A hardening pass (two robustness findings on this path) found the
Stage-A loader honoured the "a broken manifest must never brick a spawn — falls through to
fused/blockout" contract only in the happy path. Two holes were closed; the fallback promise now
holds against partial assets AND malformed-but-parseable manifests. Files touched:
`Code/Vehicle/Parts/PartKitManifest.cs`, `Code/Vehicle/Parts/PartKitAssembler.cs`,
`Code/Vehicle/VehicleFactory.cs`, plus a new offline gate `tools/test_partkit.py` +
`tools/fixtures/partkit/*.json`.

### 11.1 Transactional body assembly (was: partial loads accepted)

`PartKitAssembler.TryBuildBody` previously created a part GameObject per successfully-loaded
model and returned `built > 0` — so if one of a dozen parts loaded, the factory took the kit path
and shipped an incomplete render/collision assembly (nondeterministic, asset-compile-order
dependent). It is now two-phase:

1. **Preload + validate** every body model with `Model.Load` **before creating any GameObject**.
2. **Commit**: only once the required set is fully in hand are the "Kit Body" GO and part GOs
   created, inside a `try` that `Destroy()`s the partial body and returns `false` on any
   unexpected engine error.

On a **required** part failing to load, phase 1 returns `false` with **zero objects created**, so
`VehicleFactory` falls through to the fused/blockout path exactly as promised. There is no partial
state to roll back on the normal failure path; the commit-phase `try/catch` is belt-and-braces for
engine-level surprises.

**Required vs optional decision.** A new optional `required` field (a nullable bool) may be set
per part in the manifest. When **absent**, the default falls out of `kind`:

- **Structural / collision-bearing kinds default REQUIRED**: `chassis`, `door`, `hood`, `trunk`,
  `tailgate`, `bed`, `bolton`, `fascia`. A missing model here means missing collision extremities,
  so the whole kit degrades to the fully-featured fused body rather than a holed one.
- **Cosmetic kinds default OPTIONAL**: `mirror`, `accessory` (rollbar). A missing mirror/rollbar
  model is skipped with a warning and the kit still assembles — losing a cosmetic bit is a better
  outcome than dropping the entire kit look to fused.
- A manifest may override either way with an explicit `"required": true|false` on the part
  (e.g. `ok_explicit_required_flag.json` marks a bumper optional).

This is an **additive optional field**, so **no schema bump**: v1 (`vp.partkit/1`), v2
(`vp.partkit/2`), and v3 (`vp.partkit/3`) manifests without it remain valid and get the
kind-based default. The shipped kits omit it and behave as before, except a missing *required* model now correctly triggers
fallback instead of a partial build.

**Wheels are deliberately out of the transaction.** Wheel meshes mount later
(`MountWheelVisual`) and carry **no collider** — wheel physics is the pure raycast sim. A wheel-mesh load failure therefore degrades to an *unmeshed but fully simulated* wheel
(warning logged), never a whole-vehicle fallback. Manifest validation still guarantees the four
FL/FR/RL/RR **entries** exist; only a missing compiled `.vmdl` at load time leaves a wheel
unmeshed.

### 11.2 Strict manifest validation (was: validation stopped before indexed fields)

`PartKitManifest.TryLoad` deserialized, checked schema + non-empty `parts`, then handed the DTO
to the assembler, which indexed `dims_m[i]`, `local_bounds_*[i]`, `attach_author_m[i]` directly —
so a parseable JSON with a truncated array or missing field deserialized fine and then **threw at
spawn, outside `TryLoad`'s catch**. A full `Validate(out errors)` now runs inside `TryLoad`
(before the v1 bounds normalization) and returns `null` on any required-field error, logging every
diagnostic with kit + part name. `TryLoad` is now **un-throwable for any parseable JSON**: the
`Json.Deserialize` call is individually wrapped (wrong field types / NaN / Infinity literals throw
in System.Text.Json and are swallowed to the fallback), a null result is handled, and validation
never indexes an unchecked array.

Rules enforced (mirrored offline in `tools/test_partkit.py` — see §11.3):

- **schema** in (`vp.partkit/1`, `vp.partkit/2`, `vp.partkit/3` — v3 added with the D1
  damage band); non-empty `kit` name.
- **parts** non-empty; **unique, non-empty** `part` names.
- per part: non-empty `vmdl`; non-empty **recognized** `kind`
  (`chassis`/`wheel`/`door`/`hood`/`trunk`/`tailgate`/`bed`/`bolton`/`fascia`/`mirror`/`accessory`);
  `rotation_axis_local` ∈ {null, X, Y, Z}.
- `dims_m` present, **finite, length-3, strictly positive**.
- `local_bounds_min_m` / `local_bounds_max_m` present, finite, length-3, **min ≤ max per axis**.
  (Ordering is validated on RAW values; the v1 normalization negates-and-swaps x,y which
  **preserves** min ≤ max, and leaves z untouched, so raw ordering ⟺ consumed ordering.)
- `attach_author_m` (the CONSUMED position source) present, finite, length-3; `attach_local_m`,
  if present, finite length-3 (it is bound-but-unconsumed).
- `mass_fraction` **finite and non-negative** (still unused in Stage A, validated for Stage C).
- exactly **one** each of `wheel_fl`/`wheel_fr`/`wheel_rl`/`wheel_rr`; no stray `kind:"wheel"`
  outside that set; at least one body (non-wheel) part.

### 11.3 Offline test gate — `tools/test_partkit.py`

Because this s&box template has no cheap headless C# unit-test runner, the validator rules are
**mirrored** in `tools/test_partkit.py` (a dependency-free Python validator). It is the guard for
the `tools/gen_vehicle.py` generator contract and a regression net for the rules themselves. It
validates the two shipped manifests (must PASS) plus a battery of malformed fixtures in
`tools/fixtures/partkit/*.json` (truncated arrays, dup/empty names, missing/stray wheels, NaN,
Infinity, absent `vmdl`, negative dims/mass, unordered bounds, bad kind/axis, empty parts,
wheels-only, bad schema; must be REJECTED). Both `validate_manifest()` and
`PartKitManifest.Validate()` carry a **LOCKSTEP** comment: change one, change the other. The C#
side is authoritative at runtime; the Python side is the offline generator guard.

Run:

```
python tools/test_partkit.py            # table; exit non-zero on any mismatch
python tools/test_partkit.py --verbose  # + each rejection's error list
```

Last run (2026-07-12): **PASS — all 22 manifests matched expectation (5 pass / 17 reject)**,
each `bad_*` rejected for its intended reason. `dotnet build Code/vehicle_prototyping.csproj` is
green with **0 warnings** (the audit's unused-`m` warning at `VehicleFactory.cs:204` was also
removed as part of this pass, along with a `LoadModelWithFallback` fix: a `.vmdl` path loads
directly; an extensionless path warns and falls back instead of throwing on `LastIndexOf('.')`).

### 11.4 DEFERRED live check

Compile-green + the offline validator are the gates cleared here. The **live spawn** confirmation
is deferred. Exact recheck (editor MCP on port 7290):

```
python tools/mcp_client.py play_start --quiet
python tools/mcp_client.py console_command '{"command":"vp_spawn_kit dragstrip"}'
# expect log:  [vp] partkit 'hatch_kit' body assembled: 7/7 parts, seatZ=...  +  AUDIT partkit_bounds offenders=0
# (pickup_kit, when rostered, should log 12/12 with mirrors+rollbar present or 'N optional skipped' if a cosmetic vmdl is uncompiled)
python tools/mcp_client.py play_stop --quiet
```

Fallback proof (do once): temporarily point a kit at a manifest with a **required** part whose
`vmdl` does not resolve (or an intentionally malformed manifest from `tools/fixtures/partkit/`),
spawn, and confirm the log shows the assembly failure as a clear `[vp]` **error** **and the car
still spawns on the primitive blockout body** (box/kart + sphere wheels — no fused stand-in, no
exception, no partial mesh, no removed-asset load). Restore the manifest afterward.
