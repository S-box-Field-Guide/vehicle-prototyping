# Pre-deploy notes — MUST clear before the next publish

Owner-ordered testing-mode changes that must be reverted before the next release.
Check each item, revert, delete its entry, then delete this file when empty.

## 1. Flight recorder auto-arm (2026-07-21)
`Code/Game/RampTraceRecorder.cs` — `TestingAutoArm` is `true` so every play session
records telemetry for the ramp-collider test loop. **Set it back to `false`** before
publish (players must not write 500 KB CSVs every two minutes).

## 2. Spawn location (2026-07-21): RESOLVED, no revert needed
`Code/Game/CityBuilder.cs`: the release spawn is now IMPLEMENTED (park-closer pass,
2026-07-21 late). The `(600, 170)` ramp-test spawn is gone; the car now spawns IN the
town, one city block east of the central intersection, facing the park:
`Origin + 6*Cell + RoadWidth*0.5 = (46, 0, 0)`, Identity (facing +X = east, straight
down the avenue at the east gate and the stunt park beyond). This delivers the owner's
"reset spawn into the town" request as a real release position, so there is nothing to
revert here. Left for provenance; the only remaining pre-deploy revert is item 1.
