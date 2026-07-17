# Feature Matrix - what works, what is partial, what is out of scope

The honest living ledger of every system in this project and its real state. Truth for each row
is the code and the measured battery, not a roadmap. When this matrix and any older doc disagree,
this matrix wins.

## How to read it

Four maturity gates, left to right. A row is only as mature as its leftmost missing gate.

| Gate | Means |
|---|---|
| **Designed** | Written up in a design/scope doc. |
| **Implemented** | Real code that does the thing (not a stub). |
| **Integrated** | Wired into a live path - a normal Play meets it. |
| **In band / tested** | Behavior is measured against a target and passes, or is covered by an automated maneuver. |

Cell legend: **G** = met / in band - **Y** = partial or a documented caveat (note explains) -
**R** = known failure or gap - **-** = not applicable.

The **In band / tested** column is where the honesty lives: several maneuvers have documented reds
that are open band-authoring or physics items, not hidden. Measured numbers and the full reasoning
are in `docs/baseline-metrics.md`; the target bands are in `docs/handling-targets.md`.

---

## 1. Vehicle physics core

Evidence: `Code/Vehicle/` (`VehicleController`, `VehicleWheel`, `CarDefinition`, `TireCurve`, `Drivetrain`).

| Feature | Designed | Implemented | Integrated | In band / tested | Notes |
|---|:-:|:-:|:-:|:-:|---|
| Raycast-wheel suspension (along contact normal) | G | G | G | G | 4x substep, 200 Hz effective on a 50 Hz tick. |
| Slip-curve tire model (ratio + angle, friction ellipse, load sens.) | G | G | G | G | Parametric peaked `TireCurve`; coefficients authored as effective (surface-folded) values. |
| Drivetrain (torque curve, auto-shift, open diff) | G | G | G | G | Ground-speed-implied shifting; per-car gearing. |
| Assist levels (Casual / Sport / Sim: ABS / TC / stability) | G | G | G | Y | Works across all three; 2-state ABS duty-cycles rather than servo-holds the tire peak (documented, `baseline-metrics.md`). |
| Feel assists (wall-glance, drift-catch, spin-recovery) | G | G | G | Y | Casual+Sport chassis-level arcade layers gated off in Sim; spin-recovery (kills stale backward velocity after a handbrake spin) ships default-on at 7 m/s² (tuned 2026-07-15 on the hatch, raised from 6 to 7). `spinrecovery` maneuver covers it. |
| SI units throughout, gravity set explicitly | G | G | G | G | Convert only at the engine boundary (`Units.cs`). |
| Deterministic (no runtime RNG in physics) | G | G | G | G | Same inputs reproduce byte-identical telemetry (verified across editor sessions). |

## 2. Roster - four cars

Evidence: `Code/Vehicle/CarDefinition.cs`, `Code/Game/CarSwitcher.cs`.

| Car | Designed | Implemented | Integrated | In band / tested | Notes |
|---|:-:|:-:|:-:|:-:|---|
| Hatch (FWD street) | G | G | G | Y | Drivable, tuned; launch wheelspin over the launch-character band (see 5). Ships as its part kit. |
| Coupe (RWD sports) | G | G | G | Y | Drivable, tuned; RWD character keeps the J-turn outside its time band (see 5). |
| Kart (light, twitchy) | G | G | G | Y | Drivable, tuned; low top speed by design; a couple of band-authoring reds. |
| Pickup (heavy RWD) | G | G | G | Y | Drivable, tuned; J-turn outside its time band (see 5); best-behaved on launch. |

## 3. Proving grounds & worlds

Evidence: `Code/World/` (`TestTrack`, `PlaygroundBuilder`, `PlaygroundTerrain`), `Code/Game/GameBootstrap.cs`.

| Feature | Designed | Implemented | Integrated | In band / tested | Notes |
|---|:-:|:-:|:-:|:-:|---|
| Test track (skidpad, drag strip, brake, slalom, ramps, banked, washboard, hills, J-turn pad) | G | G | G | G | Named spawn per station; the measurement world for the battery. `proving-grounds.md`. |
| Playground world (buildings, roads, ramps, bowl, loop) | G | G | G | - | Free-driving world; not measured. |
| Live world switch + FLAT/CURVY terrain toggle (M panel) | G | G | G | G | In-place rebuild; car preserved; battery refuses to measure the playground (fail-closed gate). |
| Crash wall / destruction | G | R | R | - | Full crash/destruction simulation is out of scope for this kit; a reference-only reserved plot remains. |

## 4. UI

Evidence: `Code/UI/` (Razor + scss).

| Panel | Designed | Implemented | Integrated | Notes |
|---|:-:|:-:|:-:|---|
| Drive HUD (speed/gear cluster, km/h-mph unit setting, key hints) | G | G | G | Player-facing. Pedal bars + per-wheel grip chips live in the telemetry overlay (L), not here. |
| Session menu (Tab - resume, change vehicle) | G | G | G | Roster cycle lives here. |
| Controls / help overlay (I) | G | G | G | Auto-shows once on a fresh install; persists dismissal. Letter key — F1/Esc are host-captured. |
| World & terrain panel (M) | G | G | G | Drives the live world switch. Letter key — F2 is host-captured. |
| Tuning panel (T - live physics dials) | G | G | G | Writes onto the running car; reset control. |
| Telemetry overlay (L) | G | G | G | Live traces from the ring buffer. Letter key — F1-F12 are host-captured (was F4, dead in the published client). |
| Engine audio (shared placeholder loop, RPM-pitched) | Y | G | G | One 3D positional loop per car, pitch from idle-to-redline RPM, volume swells with throttle. Placeholder shared tone, not a layered engine model; three candidate loops + `vp_engine_sound` / `vp_engine_volume` console dials. `Code/Vehicle/EngineAudio.cs`. |

## 5. The maneuver battery

Evidence: `Code/Testing/`, `specs/maneuvers/`, `tools/vp_test.py`. Full measured ledger and the
reasoning behind every red: `docs/baseline-metrics.md`. Bands: `docs/handling-targets.md`.

| Maneuver | Hatch | Coupe | Kart | Pickup | Notes |
|---|:-:|:-:|:-:|:-:|---|
| Launch (0-100 time) | G | Y | G | G | Coupe 0-100 ~6.9 s vs a 5-6 s band - traction/open-diff limited (documented). |
| **Launch (wheelspin duration)** | **R** | **R** | **R** | G | Slip>0.2 low-speed transients counted aggressively; part metric artifact, part real. Over threshold on hatch/coupe/kart; pickup passes. |
| Top speed | G | G | G | G | All in class band. |
| Braking (100-0 distance) | G | G | R | Y | Hatch/coupe pass after a band re-anchor; kart band unchanged and still red. |
| Skidpad (lateral g) | Y | Y | Y | Y | Below band - now a measurement-profile artifact (fixed-throttle profile under-drives the raised grip); pickup, the only saturated car, tracks band movement. |
| Slalom | G | G | G | G | Coupe completes after a grip-appropriate cruise re-anchor (through-gate 15.28–17.50 m/s); pickup passes deterministically at the current head (15.06 m/s in band, zero cone strikes — an earlier DNF resolved by pilot hardening). Hatch and kart complete clean. |
| J-turn (handbrake 180) | Y | R | R | Y | Coupe now completes a catchable 180° (handbrake initiation lengthened 0.35→1.6 s); all three RWD cars complete catchable but sit outside the aspirational time band (grip-limit reds, re-anchor candidates). |
| Jump (airtime, landing settle, flips=0) | G | G | Y | G | Flips=0 on all; kart landing pitch marginal. |
| Wave-2 (liftoff / washboard / hillclimb / figure8) | Y | Y | Y | Y | First-measurement band-authoring reds on several rows (documented as authoring, not physics); figure8 passes on hatch/kart/pickup, coupe DNFs on the fixed-throttle circling profile. |

Standing invariant audits (flips, fall-throughs, stuck, NaN forces, sleep-while-driving) are
**0 on every run** of the battery.

### Why the reds are red (short version)

1. **Launch wheelspin (hatch/coupe/kart)** - the wheelspin metric counts slip>0.2 above a low
   velocity floor, so low-speed contact-patch slip registers aggressively; traction-control
   retargeting halved it, the residual is part metric artifact and part real launch behavior.
2. **Slalom DNFs (resolved)** - the coupe's DNF was a band-speed authoring issue (the old
   through-gate demanded a pace the car's measured grip cannot reach at the 18 m rhythm; the
   band is now anchored to measured grip), and the pickup's was cured by pilot hardening.
   All four cars now complete deterministically.
3. **Skidpad below band** - a measurement-profile artifact: the fixed-throttle skidpad no longer
   saturates the raised-grip tires. The fix is a drive-to-saturation profile.
4. Several wave-2 rows are **band-authoring** reds (bands written before any measurement existed) -
   flagged for re-anchor, not physics bugs.

None of these are hidden: they are the honest edge of a prototyping toolkit, and every one is
traced to a cause in `docs/baseline-metrics.md`.

## 6. Tooling & pipeline

| Item | Designed | Implemented | Integrated | Notes |
|---|:-:|:-:|:-:|---|
| Playtest harness (`tools/vp_test.py` + `Editor/VpTools.cs` + bridge/pilot) | G | G | G | Runs the battery unattended; prints a verdict table; non-zero exit on any fail. `testing-harness.md`. |
| Blender multi-part vehicle generator (`tools/gen_vehicle.py`) | G | G | G | Parts with joint-pivot origins; wheels mount on the simulated suspension. `part-kit-pipeline.md`. |
| Part-kit assembly path (`Code/Vehicle/Parts/`) | G | G | G | Kit cars pass the battery within tolerance of their fused twins. `part-kit-assembly.md`. |
| Multi-body wheel spike (Stage B) | G | R | R | Research item; not started in this repo. |
| Parts interaction & damage (Stage C) | G | R | R | Out of scope for this kit (no runtime destruction simulation). |

## 7. Controllers

Evidence: `Code/Vehicle/VehicleController.cs`, `ProjectSettings/Input.config`.

| Feature | Designed | Implemented | Integrated | Notes |
|---|:-:|:-:|:-:|---|
| Keyboard driving | G | G | G | WASD + handbrake + reset. |
| Gamepad tier (analog steer curve, trigger pedals, bumper handbrake) | G | G | G | Engine-native bindings in `Input.config`. |
| Racing-wheel tier | G | R | R | Feasibility-spike item; not built (DirectInput reach through the game whitelist is the open question). |
| Setup / binding wizard | G | R | R | Future UX round. |

---

## Out of scope (by design)

Full soft-body (node-beam) simulation, traffic AI, multiplayer sync tuning, open-world streaming,
true mesh deformation, and force feedback are explicitly **not** goals of this baseline. The
architecture stays multiplayer-open, but there are no multiplayer milestones here.
