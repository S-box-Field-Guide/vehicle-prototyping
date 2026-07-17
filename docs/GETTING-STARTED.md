# Getting Started

A short walkthrough: clone the repo, open it in the s&box editor, press Play, and drive.
Then how to switch cars and worlds, open the tuning panel, run the automated test battery,
and find your way around the code.

---

## 1. Open and drive (60 seconds)

1. **Clone** this repository anywhere on disk.
2. **Open s&box** (the Facepunch Source 2 editor). Choose *Open Project* and pick
   `vehicle_prototyping.sbproj` in the repo root. The project loads and compiles its two
   assemblies (game + editor); wait for the compile to finish (green).
3. The startup scene is `Assets/scenes/vehicle_proto.scene` - it opens automatically. It is a
   near-empty scene on purpose: a single bootstrap component builds the whole world, spawns your
   car, and wires the HUD when you press Play.
4. Press **Play**. You spawn in the hatch on the proving-ground test track.
5. **Drive** with `W` `A` `S` `D`. That is it - you are moving.

On your first Play a **controls / help overlay** appears. Read it or dismiss it; press `I`
any time to bring it back. (`F1` / `Esc` are stolen by the s&box host, so help is on `I`.)

---

## 2. Controls

All bindings live in `ProjectSettings/Input.config` and can be rebound there.

### Keyboard

| Key | Action |
|---|---|
| `W` / `S` | Throttle / brake + reverse |
| `A` / `D` | Steer left / right |
| `Space` | Handbrake (hold for a slide; also the "jump" action name in the input map) |
| `R` | Reset / respawn the car in place |
| `T` | Tuning panel (live physics dials) |
| `I` | Controls / help overlay (`F1`/`Esc` are host-captured) |
| `M` | World & terrain panel (`F2` is host-captured) |
| `L` | Telemetry overlay (`F1`-`F12` are host-captured — was `F4`) |
| `H` | Hide / show the drive HUD |
| `Tab` | Session menu (resume, change vehicle, etc.) |
| Mouse | Orbit look (cursor locks while driving; unlocks in menus) |
| Mouse wheel | Zoom chase camera in / out |

### Gamepad

The gamepad tier is analog and ships wired out of the box:

| Control | Action |
|---|---|
| Left stick (horizontal) | Analog steering (deadzone + softened center curve) |
| Right trigger | Throttle |
| Left trigger | Brake |
| Left bumper | Handbrake |
| `A` | Jump / reset-adjacent action |
| `X` | Reset / respawn the car |

Steering rides the left-stick axis directly, with a small deadzone and a response curve so the
center is fine and full lock is still reachable. See `Code/Vehicle/VehicleController.cs`
(`SampleGamepad` / `ApplyGamepadSteerCurve`) if you want to tune the feel.

---

## 3. Switching cars

The roster ships with four vehicles, each a distinct class:

| Id | Class |
|---|---|
| `hatch` | Front-drive street hatchback (the default; assembled from its part kit) |
| `coupe` | Rear-drive sports coupe |
| `kart` | Light, twitchy, low top speed, instant response |
| `pickup` | Heavier rear-drive pickup, torquey, longer-travel suspension |

Two ways to switch:

- **In-game:** press `Tab` to open the Session menu, then *Change vehicle* - it cycles through the
  roster (`hatch` -> `coupe` -> `kart` -> `pickup`). The new car spawns at your current position and
  heading, not back at a station.
- **Console:** `vp_car coupe` (or any roster id).

The roster is defined in `Code/Game/CarSwitcher.cs` (`Roster`).

---

## 4. Switching worlds and terrain

Press `M` for the **World & Terrain** panel. Two worlds:

- **Proving grounds (`proto`)** - the instrumented test track: skidpad circle, drag strip, brake
  zone, slalom lane, ramp set, banked curve, washboard section, hill-grade ladder, and an open
  J-turn pad. This is the *measurement* world the test battery runs against.
- **Playground** - a looser world (imported buildings, long roads, ramps, a banked bowl and a
  loop) for free driving.

The panel also has a **FLAT <-> CURVY** terrain toggle. Switching is a live in-place rebuild:
the current world is torn down and the selected one is built fresh, and your car is preserved
across the switch. (The measurement battery refuses to run on the playground world by design, so
metrics always come from the `proto` track.)

---

## 5. The tuning panel

Press `T` for the live **Tuning panel**. Its sliders write directly onto the running car's
physics - engine torque, tire-curve peaks, suspension, brake torque, the assist dials, and more -
so you can feel a change immediately without a recompile. There is a reset control to return to
the car's authored definition. The authored values themselves live in
`Code/Vehicle/CarDefinition.cs`.

### Engine audio

Every car plays a shared placeholder engine loop (`Code/Vehicle/EngineAudio.cs`): one 3D positional
tone that follows the car, pitched by the live engine RPM (each car's idle-to-redline band maps onto
roughly 0.8x-2.0x pitch) and swelled by throttle so you can hear lift-off. It is a stand-in for a
proper layered engine model, not a finished audio system. Two console dials tune it live:
`vp_engine_sound a|b|c` picks which of the three candidate loops plays, and `vp_engine_volume`
scales the master engine volume (`1` = full, `0` = mute). The loops live in `Assets/sounds/engine/`.

---

## 6. Running the test battery

The project ships with an automated playtest harness so tuning is data-driven, not vibes. A
Python runner drives the live editor through a battery of scripted maneuvers (launch, top speed,
braking, skidpad, slalom, J-turn, jump, and more), measures telemetry, and prints a pass/fail
table against per-class bands.

Quick start:

```
# Offline: validate every maneuver spec without an editor (schema + metric names).
python tools/vp_test.py --dry-run --all

# Live: run the full battery against a running editor (Play mode must NOT already be started).
python tools/vp_test.py --all

# A single maneuver across the whole roster:
python tools/vp_test.py specs/maneuvers/skidpad.json
```

The runner talks to the editor over an MCP HTTP endpoint. **The port number is only a hint** -
the s&box MCP port is a single global preference that drifts as editors open. The identity check
is the truth: the runner probes `editor_status.Project == "vehicle_prototyping"` before doing
anything. Point it at your editor with the `--url` flag or the `VP_MCP_URL` environment variable:

```
set VP_MCP_URL=http://127.0.0.1:7290/mcp        # Windows (cmd)
$env:VP_MCP_URL = "http://127.0.0.1:7290/mcp"    # PowerShell
python tools/vp_test.py --all
```

Maneuver specs are plain JSON in `specs/maneuvers/` - copy one and edit the asserts to define your
own test. The measured baselines for the shipped roster are in `docs/baseline-metrics.md`, and the
per-class target bands are in `docs/handling-targets.md`.

---

## 7. Project layout tour

| Path | What lives there |
|---|---|
| `Code/Vehicle/` | The physics core - raycast-wheel suspension, slip-curve tires, drivetrain, assists (`VehicleController`, `VehicleWheel`, `CarDefinition`, `TireCurve`), plus the multi-part assembly under `Code/Vehicle/Parts/`. |
| `Code/Testing/` + `Editor/VpTools.cs` | The playtest harness: the scripted maneuvers (`Code/Testing/Maneuvers/`), the play-mode pilot that injects inputs, the command/report bridge, and the `vp_*` editor tools the Python runner calls. |
| `Code/World/` + `Code/Game/` | World construction (test track, playground, terrain) and the bootstrap that builds everything on Play. |
| `Code/UI/` | All Razor + scss panels: drive HUD, tuning panel, telemetry, session menu, and the world / help overlays. |
| `tools/gen_vehicle.py` | The Blender part-kit generator - vehicles are authored as separate parts (chassis, wheels, doors, bumpers), not one fused mesh. |
| `specs/maneuvers/` | JSON specs for the test battery (one maneuver + car + station + pass conditions per file). |
| `docs/` | Design and reference docs - start with `feature-matrix.md` for an honest what-works ledger, `testing-harness.md` for the harness recipe, and `proving-grounds.md` for the track layout. |

---

## Where to go next

- **`docs/feature-matrix.md`** - what is built, what is partial, what is out of scope (read this
  before assuming a feature works).
- **`docs/handling-targets.md`** - the per-class metric bands the cars are tuned against.
- **`docs/proving-grounds.md`** - the test-track stations and how to spawn at each.
- **`docs/testing-harness.md`** - the full harness procedure and telemetry contract.
