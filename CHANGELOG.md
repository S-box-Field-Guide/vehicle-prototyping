# Changelog

Publish cadence: bump the publish stamp (`tools/bump_publish_stamp.py`) and append a
`## build N` section here on EVERY republish of `fieldguide.vehicle_prototyping`. No semver —
the stamp is a monotonic tester-attribution counter (see `Code/Game/VpBuild.cs`).

## build 9 — 2026-07-17
- REDESIGN Playground is now a pure STUNT PARK, distinct from the proving-grounds measurement scene.
  Removed all the town content — the imported buildings, the city arterial + connector road network,
  dashed lane paint, curbs, the start/finish gantry, and the old rounded-rectangle "loop track"
  CIRCUIT — leaving just objects to jump off. Kept the flat ground core + rolling-hills terrain, the
  banked bowl, and the sealing perimeter wall. The park is MAXED OUT with jumps of every size on the
  flat core (both terrain modes): a KICKER LADDER of five distinct heights (0.6/1.2/2.0/3.0/4.5 m,
  small→huge) side by side, colour-coded green→red with marker pylons off a long spawn runway; four
  CHAINED rhythm lines (4×1.0, 3×0.6, 3×2.5, 3×1.2 m) spaced by the real launch arc so you can link
  multiple jumps; SIDE-BY-SIDE OPPOSED gap pairs whose two lips face each other so you meet a launch
  face from either direction; a BIG-AIR set-piece (a 6 m launch off its own long runway across a gap to
  a matching landing ramp); coverage singles filling the open corners; a JUMP-ONTO-BOX (2.2 m launch →
  air gap → elevated 3.2 m drivable box → curved down-ramp off the far edge); a BALL PIT of 12 dynamic
  spheres (Rigidbody + SphereCollider, 45/20 kg) cars can punt around; and blockout extras — a tabletop,
  a drivable step-stair climb with a roll-off, and a banked wall-ride strip. Jumps follow an ORIENTATION
  LAW — in-line jumps face the same way (rhythm runs), side-by-side jumps face opposite — so no approach
  ever meets only a wedge back. Spawn on the open west apron facing east down the ladder runway.
  `Code/World/PlaygroundBuilder.cs`.
- FIX curved kicker collision now follows the actual curved SHAPE at every size — a small kicker hit at
  full speed launches the car instead of acting as a wall. The single thin collision-mesh shell was
  replaced with a stack of solid convex TANGENT BOXES, one per arc segment pitched to the local slope,
  so the wheels meet a genuine ~0° tangent at the base and a solid to ride on the whole way up (no
  thin-shell penetration, no hull/AABB simplification). Applies to every kicker in the park and the
  proving-grounds jump station. `Code/World/RampKicker.cs`.
- REMOVE the loop-the-loop (it drove as just a circle, no real loop) — its footprint is now more jumps.
- FIX Playground now spawns on the west apron at -150 m as authored, lined up down the kicker-ladder
  runway. The spawn point (authored in metres like all layout geometry) was passed to the spawner in
  metres without the metres→units conversion, landing the car near map centre; the whole spawn position
  is now converted at the boundary. Proto's spawn is unchanged (its spawn point is the origin).
  `Code/Game/GameBootstrap.cs`.

## build 8 — 2026-07-17
- NEW analog throttle & brake on the controller: the right and left triggers are now read as a true
  variable 0..1 pull (right = gas, left = brake) instead of an on/off press, so a light trigger gives a
  light pedal — you can actually meter your speed with a pad. Keyboard W/S are unchanged (still full
  on/off) and blend with the triggers per pedal, so either input works and neither overrides the other.
- TUNE hatch/coupe/pickup turn a bit easier at speed — less handbrake dependence: high-speed steer
  lock raised (hatch 8→9.5, coupe 8→10, pickup 7→8); low-speed lock and the speed-blend point are
  unchanged, and the kart is untouched.
- FIX Playground main-road edges are now drivable-over / flush with the ground: the loop-track
  corners were raised, rolled banked chords (top ~0.28 m proud, each with its own box collider) that
  formed an un-climbable lip and exposed wedges where they met the flat straights — they are now flat,
  flush, non-colliding ribbons like the straights (the bowl remains the dedicated banked feature), so
  the whole road network can be crossed anywhere it visually invites it, on Flat and Rolling Hills alike.
- FIX ramps rebuilt as curved solid kickers (concave circular-arc face) — smooth entry tangent to the
  ground (no lip), a closed body with a sealed underside (no more drive-under gap to get stuck in), and
  a collider that follows the curved face instead of a straight box wedge. Applies to the Playground
  jump field (launch kickers + tabletop) and the proving-grounds jump station; the hill-grade ladder
  keeps its straight constant-grade ramps (a grade test, not a jump). `Code/World/RampKicker.cs`.
- NEW rotating 3D car picker replaces the flat PNG thumbnails in the Session menu roster grid: each
  tile is now a live `ScenePanel` rendering the actual car model, slowly auto-rotating so every angle
  reads before you pick. Same 2×2 grid, same selection/active-tile styling — only the tile content
  changed from a static image to a live scene.
- NEW signed reverse speedo: the HUD speedometer now shows a negative value while backing up instead
  of the unsigned magnitude, and no longer flickers between 0 and 1 at creep speed in reverse.
- FIX drive mode now persists across car switches (Session menu picker and `[`/`]` cycle) instead of
  resetting to the default on every switch.
- FIX UI text-contrast pass: dim gray instructional text across the HUD/menus is now bright
  white/high-contrast per the standing text-contrast order.
- FIX pickup cab glass fit: window panes were leaning/offset inside their frames — now upright and
  properly inset.
- NEW pickup wheel hub caps.
- VISUAL kart rounded-body pass: bevels on the kart's body panels now match the rounding treatment
  already applied to the other three cars in build 7.
- NEW plain-language tuning guide (`docs/tuning-guide.md`, linked from the README) plus an in-game
  "?" explainer button on the Tuning panel (T): it opens an overlay summarizing what every dial does
  and which way to move it, for players who aren't car experts. The overlay lives in its own panel
  (`TuneHelpOverlay`) so it survives TuningPanel's every-frame rebuild; close it with the button again,
  the ×, Esc, or a click outside.

## build 7 — 2026-07-16
- VISUAL pass on three of the four cars (hatch, coupe, pickup): the box wheel-arch flares are now
  ROUNDED semi-annular arch bands that follow each wheel (per-axle sizing on the coupe, whose front
  tires are narrower than the rears; every arch keeps its outer face 2.5 mm inboard of the tire face
  so the wheel always reads proud), and the major body panels — floors, beltlines, roofs, doors,
  hoods, decks, bed walls, bumpers, spoilers/wings — gained soft beveled edges in place of razor-sharp
  box corners. Tires are rounder too (24-segment with beveled shoulders). All generated
  deterministically by `tools/gen_vehicle.py` (new `rcube` / `rcyl` / `arch_band` vocabulary);
  kit verification, coplanar census, and wheel-occlusion census all pass. The kart is untouched
  (exposed-frame design — no arches to round).

## build 6 — 2026-07-16
- NEW live PLAYER launch timer feeds the telemetry TIMING card in ordinary free driving (was static
  outside harness/pilot runs). `Code/Game/PlayerTiming.cs` — an always-on component mounted by
  `UiRig.Mount`/`Retarget` that arms at a standstill with no throttle, starts on first movement,
  streams live elapsed, captures the 0→100 km/h (or 0→60 mph) split and the ¼-mile (402.336 m) split,
  and re-arms on brake-to-stop / respawn / car switch. Unit-aware: `UserSettings.SpeedUnit == Mph`
  switches the target to 26.82 m/s and the title to "0–60 MPH" (km/h keeps "0–100 KM/H"); internals
  stay SI. Session best per car per metric (in-memory). Dormant while a scripted pilot maneuver runs
  (`VehicleBridge.Status == "running"`) so automated runs keep their display unchanged; reuses the
  existing UiFeed `Timing*` fields, so no razor change.
- REDESIGN Session menu (Tab) from a single 340px column to a 760px two-column card (design
  "Vehicle Session Menu", owner-commissioned — supersedes the old single-column design-lock for
  this panel). LEFT column: Resume (primary) + "Respawn car" (ghost, with an `R` keycap chip),
  divider, labelled Drive-mode & Units segmented controls (each now shows its current value at the
  right of its label), spacer, divider, red "Quit to menu". RIGHT column: a "Vehicle" header with a
  `[ ]` cycle hint and a 2×2 roster tile grid — thumbnail on top, name + status on the footer bar;
  the active car reads "driving", the others their 1-based roster number; the selected tile gets the
  cyan border + glow. Caption: "Switching respawns you in the new car at the same spot." No
  functional regressions: Resume/Respawn/SetAssist/SetUnit, the data-driven picker (id → ui/cars/<id>.png,
  VehiclePilot.ResolveCar), and the `vp_session` console command are all preserved.
- NEW Esc closes the Session menu (Resume). Wired via `Input.EscapePressed` get/set-consume,
  consumed only while the menu is open so other panels/the host keep their Esc. The header advertises
  it with an `Esc` keycap chip.
- NEW `[` / `]` cycle the vehicle roster (prev / next) through the same CarSwitcher path the picker
  uses — works while driving too, since it's an Input action (`CyclePrev`/`CycleNext` in Input.config,
  bound to `lbracket`/`rbracket`). Does not change the menu's open/closed state.
- NEW "Quit to menu" is now live (was a disabled stub): `Game.Close()` returns to the s&box main menu
  (a proven whitelist-safe leave path); in the editor it ends play
  mode sanely.
- FIX telemetry grip chips (FL/FR/RL/RR) rendering at wildly different sizes per state — the REAL cause
  build 5's border fix missed. The grip card's layout class was `.grip`, which the styler compiled to
  `TelemetryPanel .grip` and applied to ANY descendant with class `grip` — including a wheel chip in the
  GRIP state (`.wchip.grip`). So grip-state chips inherited the card's `padding: 24px 26px` and ballooned
  into big squares while working/sliding/air chips stayed 22×36. Renamed the card class to `.gripcard`;
  chip size is now constant in all states and only COLOR conveys grip/working/sliding/air (owner directive).
- FIX telemetry Code Error `<tiny>% is not valid with height/width/left` — near-zero physics values
  (e.g. a `1.67e-10` throttle residual) string-interpolated into inline `style=` percentages emitted
  SCIENTIFIC NOTATION, which the engine styler rejects. Added `UiFmt.Pct()` (clamp to [0,100] + F3
  invariant) and routed every computed style percentage in the HUD/telemetry/tuning panels through it,
  so degenerate values can never emit exponential or comma-decimal garbage again.
- CHANGE tuning "Saved tunes" list now SCROLLS (fixed ~3-row height, `overflow-y: scroll` on the rows
  only) instead of growing the panel down into the Drive HUD; the header + "Save current" stay fixed.
- NEW rename a saved tune: each row has an `Edit` chip that opens a rename modal (Enter saves, Esc/Cancel
  aborts, click-outside cancels). Names stay unique per car — empty is rejected (keeps the old name), a
  collision with another tune auto-suffixes " (2)". The text field lives in its OWN panel
  (`TuneRenameOverlay`) whose rebuild is decoupled from TuningPanel's every-frame refresh, so the
  `TextEntry` keeps focus while you type (the reason rename was cut from the presets MVP).

## build 5 — 2026-07-16
- FIX telemetry overlay per-wheel GRIP chips (FL/FR/RL/RR) resizing when their color state
  changed: only the `.air` state carried a border, so grounded↔airborne (and the
  size-mismatched box math around it) visibly grew/shrank the chip; every state now carries
  the same 1px border (transparent unless overridden) so box size is identical across
  grip/working/sliding/air, and the color swap now fades over 0.12s instead of snapping. Same
  one-line bug also lurked in the THR/BRK/HBK pedal-bar ABS state (`.absbar` added a border the
  base `.bar` lacked) — fixed identically.
- FIX telemetry overlay toggle moved off `F4` to `L`: the standalone/published s&box client
  captures F1–F12 for its own overlays, so the F-key toggle did nothing outside the editor (the
  same host-capture trap that already put Help on `I`, World on `M`). Help overlay + HUD tips +
  docs updated to match.
- NEW speed-unit switch (km/h ↔ mph) in the Session menu (Tab), under a new "Units" label. DISPLAY
  ONLY — the HUD speedometer converts at render (1 m/s = 3.6 km/h = 2.23694 mph); every measurement,
  test band, and telemetry capture stays SI. Persisted per-user to `FileSystem.Data`
  (`user_settings.json`, `Code/Game/UserSettings.cs`); defaults to km/h so existing players are
  byte-identical. Changing it applies immediately.
- Session menu: the drive-mode selector (casual / sport / sim) moved ABOVE the car grid and gained a
  "Drive mode" label (segmented control itself unchanged).

## build 4 — 2026-07-16
- removed the write-only `UiFeed.StationName` / `UiFeed.BestTimes` fields — dead code left
  over from the station UI that was retired in build 1; nothing read them
- coupe handling test bands re-anchored to the car's MEASURED grip (car physics unchanged —
  pilot/spec only): the slalom through-gate now targets [15.28, 17.50] m/s (it was a 30 s
  DNF chasing an aspirational sports-coupe pace the in-sim grip cannot reach at the 18 m
  rhythm), and the J-turn now completes a catchable 180° (handbrake-initiation pulse
  lengthened 0.35 → 1.6 s to break the grippy rear loose — the same lever the pickup uses)
- cleared coplanar z-fighting on the hatch and kart bodies: 8 flagged panel pairs → 0 after
  a panel-gap regen, so no more shimmering seams on those two cars

## build 3 — 2026-07-15 (republish, unlisted)
- NEW save/load named tune presets per car: the Tuning panel (T) gains Save current / Load /
  Delete over the existing dial set, persisted to `FileSystem.Data` (`tune_presets.json`) so
  tunes survive restart. Presets are per-car (a coupe tune never applies to the hatch);
  auto-named "<car> tune N". Coupled fields handled: redline stores its derived shift points,
  grip stores the multiplier (re-applied via SetGrip), gravity captured for parity.
- NEW sequential MANUAL shift mode (opt-in; AUTO stays the default): G toggles auto↔manual,
  shift up E / gamepad R1, shift down Q / gamepad L1; over-rev (money-shift) downshifts are
  blocked. Gamepad handbrake moved off L1 to A (via Jump) to free the bumper.
- car-picker thumbnails ACTUALLY packaged: build 2's wizard ran on a stale in-memory
  sbproj and dropped the `ui/cars/*.png` Resources glob despite the disk file being
  correct (KB `g-tool-editor-clobbers-hand-edited-sbproj-on-save`); editor restarted
  so the wizard reads the restored Resources

## build 2 — 2026-07-15 (republish, unlisted)
- car-picker thumbnail fix attempted (loose-PNG Resources packaging) — did NOT ship;
  see build 3
- tuning panel tracks the live car after switching
- redline dial carries shift points + per-car reset
- draggable slider tracks (click-jump + drag-scrub)
- telemetry ABS-bar console spam fixed
- NEW spin-recovery assist (tuned 7.0 default, Sim raw, tuning dial)

## build 1 — 2026-07-15 (first publish, unlisted)
- initial roster: hatch / coupe / kart / pickup part kits
- proving grounds world
- batteries
- station UI (later removed)
- Kenney fallback removed
- assets slimmed 15 -> 3.7 MB
