# Proving Grounds — Test Track Zone

`Code/World/TestTrack.cs` builds the instrumented test-track zone: on Play, the bootstrap builds
it ~600 m east of the city and the measurement battery runs against it. The layout is authored
from compile-time constants (deterministic, no runtime RNG). The **Things to verify if you edit
the track** notes below flag the engine-convention assumptions the layout depends on.

The track is built from a small set of primitive helpers (Block / Ramp / PlaceModel, static
`BoxCollider`s, dev-box models, deterministic layout).

---

## Things to verify if you edit the track (engine-convention assumptions)

1. **`Rotation.From(pitch, yaw, roll)` argument order.** Used once, in `BankedSegment` as
   `Rotation.From(0f, yawDeg, bankDeg)` (bank = roll, third arg). If you change it, verify the
   banked-curve segments actually bank around their direction of travel and don't yaw/pitch wrong.
2. **Facing convention.** Assumed `Rotation.Identity` faces world **+X**, and
   `Rotation.FromYaw(+90)` faces **+Y** (consistent with the engine's `FromYaw(+)` = LEFT/CCW
   turn convention). Every `Stations` facing value and the drag-strip/slalom/hill lane
   direction (+X) depends on this being right. Verify with one screenshot per station once
   a car can spawn.
3. **`models/city/cone.vmdl` bounds/orientation.** `Cone()` loads this model and scales
   it off `model.Bounds.Size`/`.Mins`/`.Center`. If it fails to load or its bounds are
   degenerate, the code falls back to a solid orange box automatically (`ConeOrBlock`), so
   this is a soft-fail, not a hard blocker — but the visual will look wrong until checked.
4. **`Rotation.FromPitch(-pitchDeg)` sign for ramps/hill-ladder.** The pitch is negated; if you
   adjust it, verify the ramps/hill segments actually slope *upward* along +X, not downward into
   the ground.
5. **Non-square ground `BoxCollider` stretch.** `BuildGround` uses a non-square span
   (1000 m × 600 m) with the `collider.Scale = (100,100,200)` / `Center = Down*100` idiom,
   relying on `BoxCollider` following `WorldScale` to stretch it non-uniformly. If you change the
   span, check hard landings don't tunnel, especially at the ramp set and jump/hill stations
   (X extremes of the span).
6. **Tuple-shape property assignment.** `Stations` is typed
   `IReadOnlyDictionary<string, (Vector3 posMeters, Rotation facing)>` but is assigned from
   a locally-built `Dictionary<string, (Vector3, Rotation)>` (unnamed tuple elements) — tuple
   element names are compile-time-only and convert implicitly.
7. **`GameObject.Tags.Add("low_grip_todo")`** — a tag string used by the low-grip patch (mirrors
   the `Tags.Add("road")` pattern).

---

## Station table

All coordinates are **meters, relative to the `originMeters` passed to `TestTrack.Build`**
(the track zone origin is ≈ 600 m east of world origin, i.e.
`new Vector3(600f, 0f, 0f)` — a parameter, not hardcoded in the file). `Stations[key].posMeters`
is the **world-absolute** SI-meter position (origin already added); the future `vp_spawn`
tool converts to engine units at its own placement call.

| Station key | Purpose | Local origin-relative coords (m) | Dimensions | Spawn point (local m, facing) |
|---|---|---|---|---|
| `skidpad` | 20 m-radius skidpad, painted ring (32 markers) | center (60, 150) | r = 20 m | (40, 150), yaw 90° (tangent) |
| `dragstrip` | 400 m drag strip, start line + boards @100/200/300/400 m | lane Y=40, X 150→550 | width 14 m, length 400 m | (140, 40), yaw 0° |
| `brakezone` | Brake zone: entry gate + distance boards @20/50/80 m | lane Y=40, X 550→650 | width 14 m, length 100 m | (520, 40), yaw 0° |
| `slalom` | 8 cones @ 18 m spacing, ±2.5 m weave | lane Y=−40, X 150→276 | span 126 m | (132, −40), yaw 0° |
| `ramps` | 3 ramp sizes (10°/16°/22°) + landing aprons | lane Y=−150, X 60→234 | small 10×6 m, med 12×6 m, large 14×7 m | (30, −150), yaw 0° |
| `bankedcurve` | 90° banked curve, r=45 m, 18° constant bank, 9 segments | arc center (700, 220), r=45 | 90° sweep | (647, 220), yaw 270° |
| `washboard` | Rough section: 20 transverse ridges @1.5 m spacing | lane Y=−150, X 320→350 | span 30 m, ridge 0.25×12×0.12 m | (305, −150), yaw 0° |
| `hillclimb` | Grade ladder: 9 graded ramps in a PARALLEL FAN, 5–45% in 5% steps, one grade per row (redesigned 2026-07-13 — see note below) | bases all at X=430; rows Y=−150 (5%) → −262 (45%), 14 m row pitch | 9× 20×10 m ramps, 4 m row gaps | (385, −150), yaw 0° |
| `lowgrip` | Low-grip painted patch (visual only, TODO friction hook) | center (600, −40) | 25×25 m | (588, −40), yaw 0° |
| `jturnpad` | Open pad for J-turns, striped border | center (780, 0) | 70×70 m | (780, 0), yaw 0° |
| `crashwall_reserved` | Reference-only reserved plot (crash wall + corner posts). Full crash/destruction simulation is out of scope for this kit — TestTrack still builds the plot, but no spec here spawns at it. | center (780, −160) | 40×20 m | (755, −160), yaw 0° |

Maneuver-battery mapping, for reference:
`launch`/`topspeed` → `dragstrip`; `brake` → `brakezone`; `skidpad`/`figure8` → `skidpad`;
`slalom` → `slalom`; `jturn` → `jturnpad`; `jump` → `ramps`; `washboard` → `washboard`;
`hillclimb` → `hillclimb`; `liftoff` (needs a "high-speed bend") → `bankedcurve` — **this
mapping is an assumption**, the station list doesn't name a "high-speed bend"
separately from the banked curve; confirm in-engine if `liftoff` needs a
faster, less-banked bend instead. `crashwall_reserved` stays a reference-only reserved plot:
the `crash` maneuver and destruction stack are out of scope for this kit,
so the wall is no longer built or driven here.

**Hill-ladder fan redesign (2026-07-13, wave-2).** The original 4-segment SERIAL ladder
(5/10/15/20% in one lane, X 400→510) had two defects surfaced by the first live `hillclimb`
battery: (a) 3 of 4 roster classes' rated-grade bands exceed 20%, and (b) serial ramps forced
every car to drive OVER all lower ramps en route to its rated one — each ramp's east edge is an
elevated cliff (a 40% ramp crests ~7 m up), so the run measured an obstacle course (the pickup
flipped off a crest drop; wheelspin spiked on jump landings), not grade-holding. Now 9 ramps
(5–45%) sit in PARALLEL ROWS fanning south: every base at X=430, row k at Y=−150−14k. The
`HillClimbManeuver` pure-pursuits from the (385,−150) spawn to its rated grade's row on flat
ground, aligns, and climbs that ramp alone, stopping ~75% up the slope (never over the crest
edge). `TestTrack.HillLadder` (+ `HillLaneYMeters`/`HillBaseXMeters`/`HillRampLength`) is the
single source of truth both the geometry and the maneuver read — they cannot drift apart. The
fan's south extent (Y −267 at the 45% row edge) stays on the ground plane (edge −280) and clear
of the crash lane (Y=−160 starts at X≈708; the fan ends at X≈450).

---

## Plan-view ASCII map

Local meters, origin at bottom-left of this view = `(0,0)` (i.e. `originMeters` in world
space). Not to scale in both axes — X compressed more than Y for legibility.

```
Y +220 ..................................................[bankedcurve arc]..
                                                            (700,220) r45
Y +150 ....[skidpad r20]...................................................
            (60,150)
Y  +40 .............[dragstrip 150-550]==[brakezone 550-650].................
                      width14, Y=40 lane
Y   +0 .................................................................[jturnpad]
                                                                          (780,0) 70x70
Y  -40 .............[slalom 150-276, 8 cones]......[lowgrip 25x25]..........
                      Y=-40 lane                     (600,-40)
Y -150 .[ramps 60-234]..[washboard 320-350]..[hill fan 5% row]..............
         Y=-150 service lane                     bases X=430
Y -160 .......................................................[crashwall]
Y -164   .......................................[hill 10% row]   (780,-160) 40x20
  ...      (9 hill-grade rows fan south, 14 m pitch: 5% at Y=-150
Y -262   ....................................... down to 45% at Y=-262, all bases X=430)
        X:  0    60   150  220  300  350  400  450  500  550  600  650  700  780
```

Reading order: the drag-strip/brake-zone lane (Y=40) and the slalom lane (Y=−40) run
parallel east; the skidpad sits north of the drag strip start; the ramp/washboard/hillclimb
"rough lane" runs along Y=−150 south of the slalom; the banked curve, low-grip patch,
J-turn pad, and crash-wall reserve fill the eastern end; the hill-grade fan drops south of
the rough lane. All eleven station footprints were checked pairwise for bounding-box overlap
when laid out (see coordinates above) — none overlap (the hill fan's closest neighbor is the
crash lane: Y bands −267..−145 vs −170..−150 only overlap in Y, and their X ranges 430–450 vs
708–798 are ~260 m apart).

---

## Integration notes

- **Single call site.** This is wired in from `GameBootstrap` with one call, after
  `CityBuilder.Build`:

  ```csharp
  CityBuilder.Build( Scene );
  TestTrack.Build( Scene, new Vector3( 600f, 0f, 0f ) ); // track zone ~600 m east of city
  ```

  City total span (`CityBuilder.Total`) is ≈470 m, centered on world origin, so its east
  edge sits at local X≈235. A 600 m track-zone origin leaves a wide (~365 m+) clear gap
  before the track's westmost feature (`skidpad`, local X=40–80 → world X=640–680) —
  comfortable margin, no city/track overlap.
- **No car spawn here.** `TestTrack.Build` only constructs geometry + the `Stations`
  registry; it does not spawn any `VehicleFactory` car. The future `vp_spawn` MCP tool
  (§4.1) reads `TestTrack.Stations[name]` and does its own SI-meters→engine-units
  conversion at its own placement call — this file deliberately does NOT hand out
  engine-unit positions, to keep the `* M` conversion auditable to exactly one call site
  per station (`Slab`/`Cone` inside `TestTrack.cs` for geometry, the future spawn tool for
  cars).
- **Determinism.** No `System.Random`, no per-frame state — every station is placed from
  compile-time constants, so `TestTrack.Build` is byte-for-byte reproducible across runs,
  ensuring byte-for-byte determinism.
- **Census line.** `Build` ends with `[vp] world track stations=N tris~=M` — greppable,
  matches the house convention (the `[vp]` log tag). Triangle count is a rough
  estimate (dev-box ≈12 tris, dev-plane ≈2 tris, cone model ballparked ≈120 tris), not an
  exact census — good enough for a sanity check, not a performance budget.
- **Screenshot verification recommended per station** once a car exists (the exit criterion:
  a screenshot per station, with the car spawnable at every station by MCP call) —
  this is exactly where the Uncertain APIs above (facing convention, ramp slope direction,
  banked-curve bank direction) should get caught.

---

## Units / axes discipline

The units-and-axes and scene/world conventions were reviewed before writing this file (see the
file header comment in `TestTrack.cs` for how the `* M` meters-to-units audit discipline was
applied). Nothing here was verified in-engine — everything was written and reasoned about, never
compiled or run. The "Uncertain APIs" list above is the honest record of what needs to be
confirmed (or ruled out) once this compiles.
