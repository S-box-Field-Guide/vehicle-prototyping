# Vehicle Physics Kit

Version 0.2.0 · MIT · namespace `FieldGuide.VehiclePhysics`

A raycast-wheel arcade-sim vehicle stack for s&box: contact-normal suspension, slip-curve tires
with a friction ellipse, a full drivetrain with auto-clutch and auto/sequential shifting, per-car
assist levels (Casual, Sport, Sim), a spring-arm chase camera, and placeholder engine + skid audio.
SI units end to end, deterministic physics, runs on primitive blockout bodies so no art is required.

Extracted from the Field Guide game Vehicle Prototyping. See PROVENANCE.md for the exact source
commit.

## Quickstart

Install from the s&box editor (Library Manager, search `fieldguide`, install the Vehicle Physics
Kit), then spawn a car at runtime:

```csharp
using FieldGuide.VehiclePhysics;

// pick or author a CarDefinition, then spawn one on the ground
var def = CarDefinitions.Hatch;
float seatZ = VehicleFactory.SeatHeightM( def );          // spawn height so it settles level
var pos = groundPoint + Vector3.Up * seatZ * Units.MetersToUnits;
var car = VehicleFactory.Spawn( Scene, def, pos, Rotation.Identity );

// point a chase camera at it (put a VehicleCamera on your camera GameObject)
var cam = Scene.GetAllComponents<VehicleCamera>().FirstOrDefault();
if ( cam is not null )
	cam.Target = car.Components.Get<VehicleController>();
```

A player then drives with the input actions below. The demo scene
(`Assets/scenes/vehiclephysics_demo.scene`, driven by `DemoBootstrap`) spawns one car per roster
entry on a flat pad and is the fastest way to see the kit run.

## What you get (consumer surface)

- `VehicleFactory.Spawn( Scene, CarDefinition, position, rotation )`: builds a drivable car
  (rigidbody, box collider, four raycast wheels, controller, audio). Returns the root GameObject.
- `VehicleFactory.SeatHeightM( CarDefinition )`: suspension-equilibrium spawn height (SI m).
- `CarDefinition`: the full tuning spec for one car (chassis, wheels, tires, drivetrain, brakes,
  steering, assists, audio, driver pose). `CarDefinitions` has a 4-car demo roster
  (`Hatch`, `Coupe`, `Kart`, `Pickup`).
- `VehicleController`: the per-car driver; exposes `Definition`, `Wheels`, `Drivetrain`, `Assists`,
  live `Throttle`/`Brake`/`Steer`/`Handbrake`, `SpeedMs`/`SignedSpeedMs`, `Respawn()`, and the
  `InputOverride` seam.
- `Drivetrain`: engine/clutch/gearbox with `Rpm`, `Gear`, `ManualMode`, `ShiftUp/ShiftDown`.
- `VehicleWheel` / `WheelVisual`: one raycast wheel and its spinning visual.
- `TireCurve`: the peaked grip curve (`Street`/`Sport`/`Offroad` presets).
- `DriveInputs`: per-frame driver intent + the live device sampler `SampleDeviceInputs()`.
- `VehicleCamera`: spring-arm chase camera.
- `EngineAudio` / `SkidAudio`: placeholder positional audio.
- `Units`: SI ↔ engine-unit constants.

## Seams (how the kit talks back to your game)

A library never reaches into game code; it exposes seams. This kit has three:

1. `VehicleFactory.CustomBodyBuilder`: `Func<Scene, GameObject, CarDefinition, bool>`. Null
   (default) builds the primitive blockout body. Set it to plug in your own body builder (for
   example a part-kit assembler that reads `CarDefinition.BodyManifest`); return true if it built.
   Physics, wheels, driver, and audio are always factory-built, so the seam only swaps the visual.
2. `VehicleCamera.CursorModalOpen`: `Func<bool>`. Null (default) = the camera keeps the cursor
   locked for drive input. Point it at your UI's "a modal is open" check so the camera yields the
   cursor while a menu is up.
3. `VehicleController.InputOverride`: `DriveInputs?`. Null = live keyboard/gamepad. Set it each
   tick to drive a car from any source (an AI, a replay, a test pilot) through the same
   input → assists → drivetrain path a human uses.

## Required Input.config actions

The live device sampler reads these action names, so your project's `ProjectSettings/Input.config`
must define them (a matching set ships in this repo's host `ProjectSettings/Input.config`). New
input actions only register after an editor restart.

| Action | Default key | Gamepad | Purpose |
|--------|-------------|---------|---------|
| `Forward` / `Backward` | W / S | left stick Y | throttle / brake (fed via `Input.AnalogMove.x`) |
| `Left` / `Right` | A / D | left stick X | steering (fed via `Input.AnalogMove.y`) |
| `Jump` | Space | A | handbrake / drift button |
| `Handbrake` | Space | (none) | second handbrake bind (OR'd with `Jump`) |
| `ShiftUp` | E | R1 | sequential up-shift (manual mode) |
| `ShiftDown` | Q | L1 | sequential down-shift (manual mode) |
| `ShiftMode` | G | D-pad down | toggle AUTO / MANUAL gearbox |
| `DriveMode` | B | D-pad up | cycle assists (Casual, Sport, Sim) |
| `Reload` | R | X | respawn the car |

Gamepad throttle/brake also read the physical trigger axes directly
(`Input.GetAnalog(InputAnalog.RightTrigger|LeftTrigger)`), so the analog pedals work whether or not
you bind the optional `GasTrigger`/`BrakeTrigger` actions. Steering and pedals are variable per
device; keyboard emits exact ±1.

## Console dials

- `vp_engine_volume` (0..1): engine audio master volume.
- `vp_engine_sound`: dev override to force one engine loop on every car (empty = per-car default).
- `vp_skid_volume` (0..1): skid audio master volume.

## License

MIT. See LICENSE. All code here is Field Guide's own; the bundled engine and skid sounds are CC0
(see `Assets/sounds/engine/ATTRIBUTION.md`).
