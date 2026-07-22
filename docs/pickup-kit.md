# Pickup truck part kit

Status: **live-verified 2026-07-12**.
Contract: `docs/part-kit-pipeline.md` +
`Assets/models/vehicles/pickup_kit/manifest.json` (schema **vp.partkit/2** — see §3)

Design intent: make it look as much like a pickup truck as possible, even if that means
more Blender objects — balancing responsiveness, customization, visual fidelity, and
performance. Budget guardrails: **≤ ~8k tris total, ≤ ~16 parts** (perf
truth from earlier density studies: part/collider COUNT dominates cost, not tris
— so the design spends its 16-part budget on customization seams and its tri headroom on
silhouette).

---

## 1. Design — part list & budgets (written before modeling)

Full-size-pickup proportions, scaled to the roster: **5.40 × 2.00 × 1.90 m**, wheelbase
**3.40 m**, track **1.70 m**, wheel radius **0.35 m** (chunky 0.28 m-wide offroad profile),
visible frame rails and ~0.34 m of daylight under the body. The visual signature is the
**cab / separate bed with a real air gap between them** (~0.1 m: cab back wall ends at
author X −0.60, bed front face starts −0.68), plus the long horizontal hood line and the
upright greenhouse.

Where the parts budget went — every part is a customization seam first, a mesh second:

| # | part | why it is a separate part (customization seam) | pivot (Stage-C joint) | est. tris |
|---|---|---|---|---|
| 1 | `cab` | body-color swaps; the chassis anchor | footprint centre @ ground | ~700 |
| 2 | `bed` | THE pickup signature: swap for flatbed/camper/box later; breaks off as one unit | bed-front frame mount | ~270 |
| 3 | `tailgate` | hinged at BOTTOM edge (drops open) — the classic truck interaction | bottom hinge line | ~40 |
| 4 | `hood` | hinged at cowl, same contract as hatch kit | rear hinge line | ~40 |
| 5–6 | `door_l` / `door_r` | hinged front doors; `door_r` gets properly MIRRORED geometry (fixes the hatch-kit handle-inboard quirk at the generator level) | front hinge line | ~60 ea |
| 7–8 | `bumper_f` / `bumper_r` | chrome bolt-ons with tow hooks / step pad — first crash-detach candidates | mount plane | ~40 ea |
| 9 | `grille` | front-fascia swap seam (grille + headlight bezels as ONE cosmetic unit); introduces the `light_f` palette entry noted in `part-kit-assembly.md` §10 | mount plane | ~110 |
| 10–11 | `mirror_l` / `mirror_r` | cheap tris, big silhouette win; snap-off cosmetic parts | door-mount point | ~40 ea |
| 12 | `rollbar` | the optional-accessory demo: roll bar + light pods over the bed front — proves the kit can mount/omit accessories | bed-rail mount | ~80 |
| 13–16 | `wheel_fl/fr/rl/rr` | shared `wheel.obj`; chunkier tire with tread blocks | hub centre | ~290 ea |

**16 parts total (12 body + 4 wheels), 13 unique OBJ meshes.** Estimated ~2.7k tris — well
under the 8k guardrail on purpose: tris were spent where silhouette lives (wheel
arches/flares, tread blocks, cab-bed gap walls, grille bars, mirrors) while flat panels
stay single cubes. Actual census in §5.

Trade-offs made:

- **Part count over mesh fusion**: mirrors and grille could have been fused into cab (~3
  fewer GameObjects/colliders) but each is a real customization/damage seam; 16 parts is
  exactly the guardrail and the Stage-A precedent showed 11 parts assemble with no
  measurable cost.
- **Tri headroom deliberately unspent** (~2.7k of 8k): the render cost truth says tris are
  nearly free at this count, but low-poly reads consistent with the hatch kit and the
  Kenney world; extra tris went only into tread blocks + flares + grille where the
  silhouette pays.
- **Rear arches live in the BED mesh** (flares + inner wheel humps) so the bed detaches as
  one believable unit; the authentic bed wheel humps double as suspension-travel clearance.
- **Mirrors excluded from the width-envelope check** (they overhang to ±1.16 m like real
  truck mirrors; spec width 2.00 m is body width).

Author-frame layout (metres, +X fwd / +Y left / +Z up, ground z=0):

```
x: +2.70 bumper_f face | +2.50 bumper mount | +2.56 grille mount | +2.51..+1.05 hood
   +1.70 front axle | +1.02 cowl | +0.80 door hinge (A pillar) | -0.60 cab back wall
   -0.70 bed front wall (~0.1 m cab-bed GAP) | -1.70 rear axle | -2.55 tailgate hinge
   -2.60 bumper_r mount | -2.70 rear face
z: 0.34 frame-rail bottom | 0.35 hub height | 0.61 bed floor | 1.09 hood top
   1.10 bed rail | 1.20 beltline | 1.90 roof
```

Notable geometry decisions found during authoring:
- **Door windows are offset INBOARD to the pillar line** (author ±0.79) while the door
  skin sits at ±0.92 — the pane reads as part of the greenhouse but swings with the door
  at Stage C; the cab therefore has NO fixed side-glass strips (unlike the hatch kit),
  eliminating a coplanar z-fight by construction.
- **Wheel tread lugs stay inside the tire radius** (rotated-cube corners max at r 0.343
  < R 0.35) and poke 2 cm out of each SIDEWALL instead: the manifest diameter — which
  `PartKitAssembler`'s self-correcting visual scale trusts (`dims_m[1]/2` vs def radius)
  — stays exactly 2R, so no spurious rescale/warn, while the lug ring still reads chunky.
- **Nothing may pass author |x| = 2.70 (length/2)**: the rear hitch stub is flush with
  the bumper rear face and the tow hooks sit BELOW the front bumper bar (first drafts
  had the hitch breaking the length envelope and the hooks invisible inside the bar).
- Generator-level fixes to the shared builders (take effect on the NEXT hatch regen,
  hatch assets untouched now): `build_door` properly mirrors right-side geometry
  (handle outboard — closes the doc §10 quirk), and hatch headlights use the new
  `light_f` palette entry instead of rear-lamp red (closes the doc §10 red-herring note).

## 2. Tuning rationale (CarDefinitions.Pickup)

Class brief: ~1900 kg body-on-frame truck, RWD, torquey low-rev engine, longer-travel
softer suspension, offroad-leaning tires, 0.35 m wheels. Reasoning per field (CarDefinition semantics):

| field | value | reasoning |
|---|---|---|
| Mass | 1900 kg | full-size-pickup class (roster's heaviest; Coupe 1420) |
| BodySize | 5.2 × 1.95 × 1.55 | root belly-collider box, kept INSIDE the true extremities (bumpers ±2.70, flares ±1.00) so the bumper/door part colliders are the outermost crash contact; z is cab-mass height not roof |
| Wheelbase / Track | 3.40 / 1.70 m | MUST match kit spec (assembler audits def-vs-kit hub positions at 1 cm) |
| RideHeight / GroundClearance | 0.42 / 0.22 m | high ride is the class signature; equilibrium seat height works out to ~0.90 m root-above-ground |
| CenterOfMassDrop | 0.15 m | higher CoM than hatch (0.20 drop on a lower root) = truck-like body roll, but track 1.70 keeps the static rollover threshold > 1.1 g vs a 0.75 g skidpad band |
| WheelRadius / WheelInertia | 0.35 m / 2.4 kg·m² | kit spec; inertia scales ~r² × heavier tire vs hatch 1.2 |
| SuspensionTravel | 0.24 m | longest in roster (hatch 0.18) — the offroad/washboard strength |
| SpringRate | 42000 N/m | SOFTER relative to weight than hatch: ride frequency 1.50 Hz vs hatch 1.73 Hz; static compression 0.12 m leaves half the travel for bumps |
| DamperRate | 3400 N·s/m | ζ ≈ 0.38 of critical (2√(k·m/4)) — soft-truck float without wallow |
| LongitudinalCurve | TireCurve.Offroad | already in-repo (peak 0.90 @ κ0.14, tail 0.75 @ 0.60) — lower peak, longer progressive slide = offroad rubber |
| LateralCurve | (0.15, 0.88, 0.60, 0.76) | peak below hatch's 1.00 → skidpad band 0.70–0.80 g; wide peak-to-tail = gradual push, not snap |
| LoadSensitivity | 0.07 | heavy truck loses a bit more grip per unit load than hatch (0.06) |
| Layout | RWD | class-defining |
| PeakTorque / RedlineRpm | 320 N·m / 4700 rpm | torquey low-rev: > 2× hatch torque, lowest redline in roster; gear-1 drive force ≈ 13.5 kN ≫ what grip can use (launch is traction-limited, in character) |
| IdleRpm / ShiftUp / ShiftDown | 650 / 4100 / 1700 | low-rev diesel-ish character |
| EngineInertia / EngineBrakeTorque | 0.5 kg·m² / 90 N·m | big rotating assembly; strong engine braking downhill |
| GearRatios / FinalDrive | 3.8, 2.3, 1.5, 1.1, 0.85 / 3.9 | gear-5 tops out ~185 km/h geared; power keeps actual top in the 140–165 band; 100 km/h arrives in gear 3 near redline → 0–100 lands ~10–12 s |
| BrakeTorque / Bias | 4200 N·m / 0.65 | scaled from hatch by mass × radius then shaved ~7% so 100–0 reads "a bit longer than hatch" at equal pedal; front-heavy bias (unladen truck bed = light rear axle) |
| HandbrakeTorque | 5000 N·m | proportional to rear-axle load |
| MaxSteerAngle / HighSpeed / RateScale | 27° / 7° / 0.9 | slow-truck steering; long wheelbase already stabilizes |
| ReverseSpeedCap | inherits kit default 20 m/s (override removed, +20% speed pass 2026-07-21) | tall reverse gear + low redline self-limit the truck to ~12 m/s (~26 mph) reverse; slowest in roster by gearing character, not an artificial clamp |
| DefaultAssists | Casual | fleet default |

Signature strength: **hill grade**. Required force at 45% grade ≈ 8.0 kN; gear-1 drive
force 13.5 kN and grade load transfer ONTO the driven rear axle make the pickup the
roster's strongest climber (band 40–45% vs Coupe 35–40%) — torque and RWD-uphill physics
both point the same way, so the band is a real prediction, not a vibe.

## 3. Manifest schema v2 — generator emission fixed to the proven mapping

`gen_vehicle.py` previously emitted the DISPROVEN frame fields (det(−1) mirror
`author_to_local`, 180°-yawed `local_bounds_*` — docs/part-kit-assembly.md §2). Emission was
fixed to the empirically proven chain:

- exporter `o = (bX, bZ, −bY)`; import `m = (oZ, oX, oY)`; net **`m = (−bY, +bX, bZ)`**;
  nose at model-local **+Y**; kit-body facing yaw **−90°**.
- `attach_local_m` and `local_bounds_*` now computed with the proven mapping;
  `frames.chassis_local` text corrected.
- Schema id bumped to **`vp.partkit/2`** so the C# loader can tell corrected manifests
  from the landed hatch v1: `PartKitManifest.TryLoad` now normalizes v1 bounds
  (negate x/y — the old BoundsCenterM correction, applied once at load) and consumes v2
  as-is. `attach_author_m` remains the position source of truth on both schemas (it is
  frame-agnostic authoring data). The landed hatch_kit assets are NOT regenerated in this
  task; the hatch spec stays regeneratable and would emit clean v2 next time it runs.

## 4. Assembly changes

- `PartKitAssembler.BodyParts` (hardcoded hatch list) → manifest-driven: every part with
  `kind != "wheel"` is built as a rigid body part. Hatch assembly is byte-identical in
  effect (same 7 parts); the pickup's new kinds (`bed`, `tailgate`, `fascia`, `mirror`,
  `accessory`) need no assembler knowledge — kind stays a Stage-C semantic tag.
- `vp_spawn_kit [station] [car]` — second optional arg (`hatchkit` default, `pickup`)
  so the live harness can spawn the truck without touching the frozen pilot files.
- `CarDefinitions.Pickup` added (full def, §2 values; PartKitManifest points at
  `models/vehicles/pickup_kit/manifest.json`).

## 5. Part & tri census vs budget

Generator-measured (fan-triangulated OBJ faces; the in-script battery enforces both
guardrails as PASS/FAIL checks):

| budget | measured | guardrail |
|---|---|---|
| parts (GameObjects at assembly) | **16** (12 body + 4 wheels) | ≤ 16 ✓ (at cap by design — parts are the customization currency) |
| total tris (assembled vehicle) | **1948** | ≤ 8000 ✓ (24% of budget) |
| unique meshes / draw-call shapes | 13 OBJs | — |

Per-mesh census: cab 340 · wheel 252 (×4 = 1008 assembled) · bed 156 · grille 96 ·
rollbar 60 · door_l/door_r 48 ea · bumper_r 36 · tailgate 36 · mirror_l/mirror_r 36 ea ·
bumper_f 24 · hood 24.

The tri headroom is deliberate (see §1 trade-offs): perf truth says COUNT of
parts/colliders dominates, and 16 parts is the real budget line we sit exactly on.

## 6. Verification

| check | how | status |
|---|---|---|
| generator runs clean, in-script battery passes | headless Blender 5.1.2 run, 41/41 checks | ✅ ALL PASS (usemtl/vt ×13, envelope 2.02/5.40/1.90 vs spec, wheelbase/track exact, attach_local ≡ author_to_local, pivot semantics ×7, wheel hub centred) |
| tri census ≤ 8000, parts ≤ 16 | generator census (§5) | ✅ 1948 / 16 |
| manifest on PROVEN frame mapping | schema `vp.partkit/2`, in-script attach_local ≡ (−bY,+bX,bZ) check | ✅ |
| game assembly builds | `dotnet build Code/vehicle_prototyping.csproj` | ✅ green (only the pre-existing CS0219 warning) |
| hatch kit unaffected | `git status`: zero changes under `Assets/models/vehicles/hatch_kit/`; v1 manifest loads through the new normalization path (BoundsCenterM value chain identical by algebra: −(min+max)/2 on recorded == +(min+max)/2 on normalized) | ✅ |
| contact sheet | `docs/images/pickup_kit_contact.png` (13 meshes framed) | ✅ |
| pickup spawns + assembles 12/12 body parts | live, BOTH spawn paths: `vp_spawn_kit dragstrip pickup` AND the pilot's own `vp_drive {car:"pickup", station:"dragstrip"}` (new ResolveCar arm) | ✅ `12/12 parts, seatZ=0.888m` (= predicted equilibrium) + `AUDIT partkit_bounds offenders=0` on both |
| facing (frame lesson: asymmetric-feature check) | front34: grille bars + `light_f` headlights + chrome bumper + tow hooks at +X; rear34: tailgate band, red taillights, step bumper at −X | ✅ |
| screenshots front34 / rear34 / side | `screenshots/m4b/m4b_pickup_front34.png`, `m4b_pickup_rear34.png`, `m4b_pickup_side_profile.png` (names checked against content) | ✅ |
| city drive chase shot | `screenshots/m4b/m4b_pickup_city_chase.png` — truck IN MOTION down the city street (route op is still a stub; used a short in-place launch for movement) | ✅ |
| launch telemetry | run below | ✅ healthy |
| doors/hood/tailgate hinge MOTION | Stage C concern (mounted rigid; axes + signs recorded, tailgate open_sign flagged for visual verify) | ⛔ Stage C |

Launch run (dragstrip, pilot-spawned `car=pickup`, 2026-07-12):

| metric | value | vs band / note |
|---|---|---|
| zeroToHundredS | **9.90 s** | band 10.0–12.0 s — 1.0% under the fast edge; flagged for the baseline pass (band doc revision rule #1: baseline pass decides band-vs-car, not pre-baseline tuning) |
| wheelspinS | 2.76 s | under the global 3 s ceiling; heavy-but-hooking RWD launch on offroad rubber, in character |
| maxSpeedMs / gear / rpm | 27.78 / 4 / 3378 | plausible low-rev gearing at 100 km/h |
| pitch / contact loss | 0.85° / 4.4% | soft long-travel squat; brief unloading during wheelspin |
| flips / fallThroughs / nanForces / sleepWhileDriving | 0/0/0/0 | clean |
| stuckTicks | 6 | spawn settle-freeze, identical to the twins |

Known cosmetic quirk (benign, logged 4× per spawn): wheel visual auto-rescaled +1.4% —
the 18-vertex tire's bbox is vertex-aligned on one radial axis (0.700 m) but FACE-aligned
on the axis `dims_m[1]` measures (0.689 = 2R·cos(π/18)), so the assembler's
self-correcting scale fires by design and lands the visual at exactly r 0.35. Silencing
it means a 20-vert tire (vertex phase aligns, like the hatch's) or reading
max(dimsY,dimsZ) — deferred, cosmetic.

## 7. Live checks (EXECUTED 2026-07-12 — sequence kept for re-runs)

Play-mode note: if another editor session already holds play mode, `play_start` errors
`Already playing`. Before taking play mode, confirm `vp_status` shows `bridgeState` idle AND
`isPlaying:false` on two checks ~3 min apart — and even then a race is possible (observed
2026-07-12: two idle checks 3 min apart still lost the race in the gap before `play_start`,
which correctly errored `Already playing`). Treat that error as contention-lost and back off;
never `play_stop` a session you did not start. Exact queued sequence (editor MCP on 7290):

```
python tools/mcp_client.py vp_status                     # twice, ~3 min apart, must be idle
python tools/mcp_client.py play_start --quiet
python tools/mcp_client.py console_command '{"command":"vp_spawn_kit dragstrip pickup"}'
#   expect log: [vp] partkit 'pickup_kit' body assembled: 12/12 parts + AUDIT partkit_bounds offenders=0
python tools/mcp_client.py screenshot '{"path":"screenshots/m4b/m4b_pickup_front34.png"}'   # + camera moves
#   ... rear34, side_profile (name them m4b_pickup_rear34.png / m4b_pickup_side_profile.png)
#   FACING CHECK (frame lesson): head-on shot must show GRILLE + light_f headlights at +X,
#   taillights/tailgate at -X — position-correctness alone proves nothing.
python tools/mcp_client.py console_command '{"command":"vp_spawn_kit city pickup"}'         # city chase shot
python tools/mcp_client.py screenshot '{"path":"screenshots/m4b/m4b_pickup_city_chase.png"}'
python tools/mcp_client.py vp_drive '{"argsJson":"{\"op\":\"maneuver\",\"maneuver\":\"launch\"}"}'   # no station = in place; respawn at dragstrip first
python tools/mcp_client.py vp_drive '{"argsJson":"{\"op\":\"status\"}"}'                    # poll to done; read 0-100/wheelspin vs the 10-12 s band
python tools/mcp_client.py play_stop --quiet
```

(If harness changes altered `vp_drive`'s contract by then, the spawn +
screenshot set is still valid standalone; only the telemetry read depends on the harness.)
