---
date: 2026-07-21
problem: persist the player's drive-mode choice across sessions, and raise the Hatch's engine torque ~30% without reintroducing the historical high-torque wobble
tags: [game, persistence, drivetrain, tuning, sbox-worktree]
---

## Symptom

Two owner asks that looked simple but each had a hidden fork: (1) "save the drive mode
between sessions" -- the assist level (Casual/Sport/Sim) is written from two DIFFERENT
code paths, one of which is vendored kit code that's off-limits to edit; (2) "the Hatch
feels too slow" -- raising `PeakTorque` on a car whose class of car (high-torque FWD/RWD)
has a documented history of a wobble bug from drive torque overshooting wheel omega
within a physics substep. Also hit a pure-mechanics snag before either feature could even
be tested: `dotnet build` had no `.csproj` to build in a fresh git worktree.

## Root cause / key insight

1. **Worktree has no build files.** This s&box project's `.csproj` files are engine-
   generated and gitignored (`*.csproj` in `.gitignore`). They exist in the main checkout
   because the editor generated them once, but a `git worktree add` clones tracked files
   only -- the untracked `.csproj` never comes along. Worse, the generated files use
   RELATIVE paths back to the sbox install (`../../../../../../../Program Files.../sbox/...`),
   and the number of `../` segments encodes the checkout's folder depth from `C:\`. The
   main checkout sits at `dev/vehicle_prototyping/Code/` (7 levels to `C:\`); the worktree
   sits at `dev/worktrees/vehicle_prototyping--owner-qol/Code/` (8 levels, one deeper
   because of the extra `worktrees/` segment). Copying the main checkout's `.csproj`
   verbatim would silently resolve one directory short and fail to restore.

2. **Two writers, one off-limits.** `VehicleController.Assists` is set directly by
   `Code/UI/SessionMenu.razor` (`SetAssist`) AND by `VehicleController.CycleDriveMode()`
   inside `Libraries/fieldguide.vehiclephysics/Code/VehicleController.cs` -- vendored kit
   code that's out of scope to edit. There is no single choke point to hook a "changed"
   event.

3. **This kit's drivetrain has no aerodynamic drag term.** Top speed in top gear is set
   purely by `Drivetrain.RedlineWheelSpeed` (`RedlineRpm` / `FinalDrive` / `GearRatios` /
   `WheelRadius`) -- the rev limiter zeroes torque once RPM crosses redline, and with
   nothing pushing back against a coasting car, speed converges to that gearing-set
   ceiling regardless of how much torque got it there. And Casual traction control
   (`ApplyTractionControl`) is a closed-loop slip controller (target slip 0.14, cuts
   throttle by `slipTarget/worstSlip`), not a fixed torque ceiling -- it responds to
   MEASURED slip, so it inherently scales its own authority with however much torque
   shows up. The actual backstops against a torque-driven wobble are unrelated to TC:
   the per-substep drive-omega clamp and the 90%-of-cap smoothstep torque rolloff in
   `VehicleWheel.cs`, both independent of `PeakTorque`'s magnitude.

## Approach that worked

For the worktree build: regenerated all three project's `.csproj` files
(`Code/vehicle_prototyping.csproj`, `Libraries/fieldguide.vehiclephysics/Code/vehiclephysics.csproj`,
and would apply the same to `Editor/vehicle_prototyping.editor.csproj` if needed) using
**absolute** paths to the sbox install (`C:/Program Files (x86)/Steam/steamapps/common/sbox/...`)
instead of relative `../` chains. Absolute paths don't care how deep the checkout sits,
so they work identically in the main repo, any worktree, or a future clone. Safe to do
freely because these files are gitignored scaffolding, not tracked source -- regenerating
them changes nothing a reviewer will ever see in a diff.

For drive-mode persistence: rather than trying to intercept both writers, added ONE
polling `Component` (`Code/Game/DriveModePersister.cs`) that reads `Target.Assists` every
`OnUpdate` and diffs against the last-seen value, mirroring any real change into
`UserSettings.AssistLevel`. This is the identical technique `VehicleHud.razor` already
uses (`_lastAssist`) to detect a drive-mode change for its press-flash -- just pulled out
into its own non-UI Component, wired through `UiRig.Mount`/`Retarget` next to
`PlayerTiming`. A seed-on-first-frame guard (skip the first `OnUpdate` without treating it
as a change) stops a car retarget/Tab-switch from ever being misread as a real change,
because `CarSwitcher.SwitchTo` already carries the live session's `Assists` value forward
across a swap.

Storage-wise, extended `UserSettings.cs` (which already held `SpeedUnit` as a precedent
for "one global player preference") rather than building a new `TunePresets.cs`-style
per-car list -- the DTO shape should match the data's cardinality, and `UserSettings.cs`'s
own docstring explicitly invited new fields to join it.

For the torque increase: verified the two safety mechanisms in code BEFORE changing the
number (`VehicleWheel.cs` `IntegrateWheelSpin` drive-omega clamp, and the
`DriveRolloffOnset = 0.90f` smoothstep rolloff), confirmed they're roster-wide and
independent of `PeakTorque`, then reasoned through the TC interaction (closed-loop slip
control absorbs more torque without extra protection) and the topspeed interaction (no
drag term -> topspeed is gearing-set, not torque-set -- extra torque mostly shows up as
a faster climb to the SAME asymptote, not a higher one, unless the baseline run was
distance/time-limited before reaching it). Cross-checked against `docs/baseline-metrics.md`
that the hatch already sits mid-band at topspeed (unlike the coupe, which the specs
explicitly flag as runway-limited) -- weak evidence the hatch's topspeed number won't
move much even though it's still named as a battery band to watch.

## Dead ends ruled out

- Copying the main checkout's `.csproj` files into the worktree unmodified -- looked like
  the fast path, would have failed restore/build one directory level short.
- Hooking `SessionMenu.SetAssist` alone to persist on change -- misses the kit's own
  D-pad-up/B cycle entirely, so the "still Sport after cycling with the pad" case would
  silently not persist.
- Writing a brand-new `driver_prefs.json`/DTO file mirroring `TunePresetStore` -- doable,
  but overkill and a worse fit than extending the already-existing single-global-value
  store (`UserSettings.cs`) that was built for exactly this kind of addition.
- Assuming more torque directly raises top speed -- true for a real car with aero drag,
  false for this kit; would have overclaimed a topspeed improvement that the physics
  doesn't actually predict.

## Rule for next time

Before `dotnet build` in ANY s&box git worktree of this project, check for `.csproj`
files first (`find . -iname "*.csproj"`) -- if absent, regenerate them from a working
checkout's copy but rewrite every `Program Files`/sbox-install relative path to an
absolute path (don't just recount the `../` depth; absolute is strictly more robust and
just as gitignored-safe).

When a value can be mutated from multiple sites and at least one of them is off-limits to
edit (vendored/kit code, another agent's file, etc.), don't chase every writer -- poll the
value itself once per frame from a place you own, with a seed-on-first-frame guard so a
retarget/respawn isn't misread as a player-driven change.

When tuning this kit's engine numbers (`PeakTorque`, gearing), remember there is no drag
model: top speed is set by `RedlineRpm`/`FinalDrive`/`GearRatios`/`WheelRadius`
(`Drivetrain.RedlineWheelSpeed`), not by torque. A torque change should be predicted to
move 0-60/mid-range acceleration bands more than topspeed bands, UNLESS the specific
car's baseline run is known to be distance/time-limited before reaching its redline
asymptote (check `docs/baseline-metrics.md` / the maneuver's `note` field for a
"runway-limited" flag like the coupe has). The two universal safety backstops against a
torque-driven wobble are the per-substep drive-omega clamp and the 90%-of-cap smoothstep
rolloff in `VehicleWheel.cs` -- both roster-wide and torque-magnitude-independent, so
citing them (not re-deriving them) is enough to clear a torque-bump safety check.

## Pointers

- `Code/Game/DriveModePersister.cs` (new), `Code/Game/UserSettings.cs`,
  `Code/UI/UiRig.cs`, `Code/Game/GameBootstrap.cs` -- drive-mode persistence, commit
  `cef8488` on branch `owner-qol`.
- `Code/Vehicle/CarRoster.cs` (Hatch `PeakTorque`) -- torque increase + full reasoning in
  the code comment there, commit `c30e3a6` on branch `owner-qol`.
- `Libraries/fieldguide.vehiclephysics/Code/VehicleWheel.cs` (~line 206, 222-229, 247-253)
  -- the rolloff/clamp safety mechanisms.
- `Libraries/fieldguide.vehiclephysics/Code/Drivetrain.cs` (`EngineTorqueAt`,
  `RedlineWheelSpeed`, `Simulate`) -- the no-drag torque/topspeed model.
- `Libraries/fieldguide.vehiclephysics/Code/VehicleController.cs`
  (`ApplyTractionControl`, `CycleDriveMode`) -- TC closed-loop logic and the kit-side
  drive-mode writer that can't be edited directly.
- `specs/maneuvers/launch.json`, `specs/maneuvers/topspeed.json`,
  `docs/baseline-metrics.md` -- battery bands and the coupe's runway-limited precedent.
