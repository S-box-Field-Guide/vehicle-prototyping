# Pre-deploy notes — MUST clear before the next publish

Owner-ordered testing-mode changes that must be reverted before the next release.
Check each item, revert, delete its entry, then delete this file when empty.

## 1. Flight recorder auto-arm (2026-07-21)
`Code/Game/RampTraceRecorder.cs` — `TestingAutoArm` is `true` so every play session
records telemetry for the ramp-collider test loop. **Set it back to `false`** before
publish (players must not write 500 KB CSVs every two minutes).

## 2. Spawn location (2026-07-21)
`Code/Game/CityBuilder.cs` — testing spawn is `(600, 170)` (100 m west of the park
entry, toward the town) for the ramp test loop. **Owner's words: "reset spawn location
back into the town" before the next release.** Reference points: park entry was
`(700, 170)`; the pre-2026-07-21 spawn was the town's central intersection. Confirm
with the owner which one "into the town" means at release time.
