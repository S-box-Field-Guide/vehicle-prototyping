# Changelog — Vehicle Physics Kit

All notable changes to this kit. Versions are display versions (minor per content publish, patch
for a hotfix; manual bumps only).

## Unreleased

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

## v0.1.0 — 2026-07-18

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
