# Provenance: Vehicle Physics Kit

This kit is a curated, renamed, seam-decoupled release of code from a Field Guide game repo. All
code is Field Guide's own (MIT). This file pins the exact source it was extracted from and is
updated on every backport.

## Source

- Repo: `vehicle_prototyping` (Vehicle Prototyping), https://github.com/S-box-Field-Guide/vehicle-prototyping
- Pinned at commit: **046ceb67541ae1451862e6b2e1d7543ca3d92662** (`046ceb6`).
- Originally extracted 2026-07-18 at `3f4a5cd`; backported to `046ceb6` on 2026-07-18 (two kart
  stuck-turn fixes, see Backport log below).
- Upstream namespace: `VehicleProto` → renamed to `FieldGuide.VehiclePhysics` here.

## Files ported (from upstream `Code/Vehicle/`)

| Kit file | Upstream path | Adaptation |
|----------|---------------|------------|
| `Code/Units.cs` | `Code/Vehicle/Units.cs` | namespace only |
| `Code/TireCurve.cs` | `Code/Vehicle/TireCurve.cs` | namespace only |
| `Code/Drivetrain.cs` | `Code/Vehicle/Drivetrain.cs` | namespace only |
| `Code/VehicleWheel.cs` | `Code/Vehicle/VehicleWheel.cs` | namespace only |
| `Code/WheelVisual.cs` | `Code/Vehicle/WheelVisual.cs` | namespace only |
| `Code/EngineAudio.cs` | `Code/Vehicle/EngineAudio.cs` | namespace only |
| `Code/SkidAudio.cs` | `Code/Vehicle/SkidAudio.cs` | namespace only |
| `Code/CarDefinition.cs` | `Code/Vehicle/CarDefinition.cs` | namespace; `PartKitManifest` field renamed `BodyManifest`; roster reduced to blockout demo defs |
| `Code/VehicleCamera.cs` | `Code/Vehicle/VehicleCamera.cs` | namespace; `UiState.AnyCursorModalOpen` → `CursorModalOpen` seam |
| `Code/VehicleController.cs` | `Code/Vehicle/VehicleController.cs` | namespace; `DriveInputs` + device sampling lifted out; crefs to game types reworded |
| `Code/DriveInputs.cs` | (lifted from `Code/Vehicle/VehicleController.cs`) | `DriveInputs` struct + `SampleDeviceInputs` + gamepad helpers |
| `Code/VehicleFactory.cs` | `Code/Vehicle/VehicleFactory.cs` | namespace; `Parts/` + `Testing/VehiclePilot` + `GameBootstrap` severed → `CustomBodyBuilder` seam; seat-height + default-driver-seat inlined |

## Deliberately NOT ported (severed at the extraction seam)

- `Code/Vehicle/Parts/**` (PartKitManifest / PartKitAssembler / PartKitCommands): the kit-car body
  assembler. The body path is now the `CustomBodyBuilder` delegate; a part-kit assembler can be
  supplied by a consumer (or shipped as an optional module in a later kit version).
- `Code/Testing/VehiclePilot.cs` and the maneuver/telemetry harness: the scripted pilot. Its
  suspension-equilibrium seat-height helper is inlined as `VehicleFactory.SeatHeightM`.
- `Code/Game/GameBootstrap.cs`: the game boot object (including its perf-timing probe).
- Vehicle art models (blockout cars use engine primitives, physics-identical).

Severance cost: the documented ~4-deletion seam plus the two seam swaps above. No physics logic was
rewritten; the wheel/drivetrain/tire math is byte-for-byte the source at the pinned commit.

## Backport policy

Upstream `vehicle_prototyping` stays canonical for physics behavior. When a feel/physics fix worth
shipping lands upstream, port it here, bump the kit version, and update the pinned commit above.

## Backport log

### 3f4a5cd -> 046ceb6 (2026-07-18): kart stuck-turn fixes

Two upstream kart "stuck turning" fixes ported:

- `7956776` cap-aware drive-torque rolloff. Auto-ported into `Code/VehicleWheel.cs` by
  `tools/sync_from_upstream.py` (mechanical; namespace rewrite only).
- `046ceb6` Casual TC floor relaxation at extreme slip. Hand-merged into
  `Code/VehicleController.cs` (seamed file). The change is confined to `ApplyTractionControl`;
  the lifted-out `Code/DriveInputs.cs` was flagged by the tool (it shares the upstream
  `VehicleController.cs` mapping) but needed no change. Code lines are byte-identical to upstream;
  the one new comment line drops upstream's em dash per the house no-em-dash rule.

Tooling gap noted: `sync_from_upstream.py` cannot auto-advance this pin because a seamed file was
hand-merged. It only diffs the upstream file across `pin..HEAD` and never compares the kit file's
content, so a seamed upstream change keeps re-flagging until the pin moves. The pin above was
therefore advanced to `046ceb6` by hand.

### Pin remap (2026-07-18)

Upstream rewrote public history the same day (commit message hygiene scrub, content
unchanged). Pins remapped: old da37616 is now 046ceb6, old 992118b is now 3f4a5cd.
Physics content at the pin is identical.
