# Changelog: Vehicle Physics Kit

All notable changes to this kit. Versions are display versions (minor per content publish, patch
for a hotfix; manual bumps only).

## Unreleased

### Fixed
- The demo pad no longer mirrors ghostly white copies of the cars. The ground used a glossy
  default material and the engine's reflection passes drop per-car tint, so reflections rendered
  untinted. The pad now ships a matte material of its own.

## v0.3.0 - 2026-07-19

Demo becomes a physics lab: live tuning and a much bigger pad.

### Added
- Demo: a live tuning lab (`DemoTuningPanel`). A small `T tuning` chip sits top-left from the moment
  you spawn; the `Tune` action (T) expands it into the lab and collapses it back. It binds to the
  car the chase camera follows and applies changes to the running car. Sliders for grip and drive
  torque (multipliers), suspension stiffness, damping, and travel, and brake force; cycles for assists
  (Casual, Sport, Sim) and tires (Stock, Street, Sport, Offroad); and a reset-to-stock button. The
  panel is demo-layer only (built by `DemoBootstrap`, present just in the demo scene) and consumes the
  `VehicleCamera.CursorModalOpen` seam so the camera yields the cursor while it is open.
- `Tune` input action (keyboard T) in the host `ProjectSettings/Input.config`.
- Demo: switch between the four roster cars with `[` and `]` (`CyclePrev`/`CycleNext`, d-pad
  left/right on gamepad), mirroring Vehicle Prototyping's bindings. The camera, the player's
  input, and the tuning lab all follow the switch; parked cars hold the handbrake. A key legend
  chip sits top-right.

### Changed
- Demo pad grown from about 200 m to about 1 km across (ground collider and visual both scaled up, top
  surface still at z=0) so testers stop reaching the edge in seconds. The void watchdog still catches a
  car driven off the new, farther edge.

### Notes
- No physics changes. The tuning lab writes only through public paths (mutating `CarDefinition`, which
  the drivetrain and brakes read live, and pushing values onto `VehicleWheel`); the physics files are
  byte-for-byte unchanged.

### Fixed (community-reported, from Vehicle Prototyping players)
- Wheels now roll the right way. The visible tyre spin was mirrored, so a car driving forward
  showed its wheels turning backwards. The spin direction is corrected; forward travel rolls the
  tread forward.
- A near-stopped car no longer skids and spins its wheels in place forever. Unless you came to a
  perfect halt, the tyres used to keep rotating and the car kept creeping, with the skid sound
  droning on. A parked, unbraked car now settles to a real stop: the wheels stop turning and the
  skid goes silent. Launching from a standstill is unchanged (the effect only acts when you are off
  the throttle and nearly stopped).
- The camera's cursor-yield seam (`CursorModalOpen`) now survives hotload: a stored delegate
  orphaned by an assembly swap is dropped and re-wired at session start instead of throwing.

### Notes
- Perceived-speed feedback ("20 km/h feels like a jog") was investigated: the kit's speed math is
  correct (world scale and the m/s the physics reports both check out), so the difference is chase
  camera presentation, not a wrong speed. No feel change was made unilaterally; camera field-of-view
  and distance are tuning dials if a stronger sense of speed is wanted.

## v0.2.1 - 2026-07-19

Hotfix from first-tester feedback (a parked demo car could move on its own).

### Fixed
- Demo: parked (non-active) cars now hold the handbrake and are input-overridden from the
  moment they spawn, not from the first bootstrap tick. This closes a one-tick window where a
  car could sample a live input device before the override landed (a resting gamepad trigger
  past deadzone reads as brake and latches reverse), and stops free-rolling parked cars from
  walking off their spawn marks.

## v0.2.0 - 2026-07-19

First published release (s&box Library Manager, org `fieldguide`).

### Demo
- Only the chase camera's car takes player input; the other roster cars hold neutral inputs
  (previously every controller sampled the same keyboard, so one W press launched all four).
- Void watchdog: a car driven off the demo pad is placed back on its spawn spot instead of
  falling forever.

Backported from upstream `vehicle_prototyping` `3f4a5cd` -> `046ceb6` (two kart stuck-turn fixes).

### Fixed
- Kart "stuck turning" at speed: turning one direction under power could lock the car into that turn
  with countersteer unable to recover. Two-part fix ported from upstream:
  - Cap-aware drive-torque rolloff in `VehicleWheel`: drive torque now fades to zero (smoothstep from
    0.90x to 1.0x of the drive-omega cap) so a driven wheel settles off the cap instead of camping on
    it and pinning slip far past the grip peak. Inert below the onset, so below-cap behavior is
    byte-identical.
  - Casual traction-control floor relaxation in `VehicleController`: once slip is deep past the curve
    tail the 0.2 throttle floor fades toward 0 (slip 1.0 -> 2.5) so TC can cut throttle enough for the
    rear to re-grip. Casual-only; below the relax start the floor stays 0.2, so all below-threshold
    behavior is byte-identical.

### Notes
- Physics behavior remains byte-for-byte the Vehicle Prototyping stack at the pinned commit (now
  `046ceb6`); see PROVENANCE.md.

## v0.1.0 - 2026-07-18 (internal, unreleased)

First extraction from Vehicle Prototyping.

### Added
- Raycast-wheel vehicle stack: `VehicleController`, `VehicleWheel`, `WheelVisual`, `Drivetrain`,
  `TireCurve`, `CarDefinition` (+ a 4-car demo roster), `VehicleFactory`, `VehicleCamera`,
  `EngineAudio`, `SkidAudio`, `DriveInputs`, `Units`.
- `VehicleFactory.CustomBodyBuilder` delegate seam for a consumer-supplied body builder (default is
  the primitive blockout body path).
- `VehicleCamera.CursorModalOpen` delegate seam for UI cursor handoff.
- `VehicleController.InputOverride` seam for driving a car from any input source.
- Demo scene (`vehiclephysics_demo.scene`) + `DemoBootstrap`: one blockout car per roster entry on
  a flat pad, chase camera aimed at the first.
- Bundled CC0 engine and skid audio sets.

### Notes
- Namespaced `FieldGuide.VehiclePhysics`; no game-code or cross-library references.
- Physics behavior is byte-for-byte the Vehicle Prototyping stack at the pinned source commit
  (see PROVENANCE.md).
