# Handling Targets — v1 (pre-baseline)

Status: **v1 — pre-baseline** · Written ahead of the measured-baseline run
(`docs/baseline-metrics.md` does not exist yet as of this writing). Every band below is a
*starting hypothesis*, not a measured result. The tuning iteration loop tunes
dials/code until each roster car lands inside its band; per the revision rule at the bottom
of this doc, any band that turns out wrong gets edited with a reason, never silently ignored.

Roster (actual figures from
`Code/Vehicle/CarDefinition.cs`, which supersede the rounder class-reference approximations in the
prose):

| Class | Layout | Mass | Real-world analog |
|---|---|---|---|
| **Hatch** | FWD | 1150 kg | compact/economy street hatchback (Golf/Civic/Fit-class) |
| **Coupe** | RWD | 1420 kg | sports coupe (3-series/Supra/Mustang GT-adjacent), performance tires |
| **Kart** | RWD | 260 kg | sport/shifter kart — **deliberately de-tuned for game feel, see below** |
| **Pickup** | RWD | 1900 kg | full-size work pickup on all-terrain tires (added 2026-07-12, pickup part kit) |

All maneuvers below are the ten core maneuvers, a subset of the full maneuver
battery (figure8 and crash are later assist-composition concerns, out of scope for this doc).
Bands are **min–max**, matched to each maneuver's station (see docs/proving-grounds.md), and computable by the
harness described in docs/testing-harness.md.

---

## Hatch — target bands

Reference basis: typical compact-hatch street-tire performance figures (the same class
references cite: ≈0.80–0.90 g skidpad, 100–0 ≈ 36–42 m, 0–100 ≈ 8–10 s). Hatch is
the roster's "everyday" baseline — bands track real-world compact-hatch numbers closely,
with no deliberate game-feel deviation.

| Maneuver | Target band | Reference basis / deviation note | Status |
|---|---|---|---|
| Launch 0–100 km/h | 8.0–10.0 s | Typical compact-hatch 0–100 time (class reference example) | v1 — pre-baseline |
| Top speed | 170–195 km/h | Typical hatchback top-end; gearing favors city torque over top speed | v1 — pre-baseline |
| Brake 100–0 m | 42–48 m | **Re-anchored** to bracket the accepted measured state (feel session 2026-07-12: braking accepted, "good place for right now"). Was 36–42 (v1 aspirational); the shared 2-state ABS duty-cycles into tail-grip, physics ceiling ≈47 m — feel verdict outranks the v1 aspirational band. | v2 — feel session 2026-07-12 |
| Skidpad lateral g @ 20 m | 0.80–0.90 g | Typical compact-hatch skidpad rating on street tires (class reference example) | v1 — pre-baseline |
| Slalom 18 m-gate, avg through-gate speed | 55–65 km/h, 0 strikes | Typical street-hatch slalom pace; understeer-biased FWD caps entry speed | v1 — pre-baseline |
| Handbrake J-turn, 180° time | 2.2–3.0 s | FWD handbrake turns are inherently slower to rotate — front wheels keep pulling through the turn; band deliberately generous/slow vs a RWD car, but must stay "catchable" per the pass shape | v1 — pre-baseline |
| Lift-off oversteer, yaw overshoot | ≤ 15°, damped within 1.0 s | FWD cars show mild lift-off tuck-in (front grip returns, doesn't step the rear out) rather than true oversteer — band reflects a muted, quickly-damped response | v1 — pre-baseline |
| Ramp-jump landing settle | < 1.3 s, 0 flips | Softer long-travel springs settle a bit slower than sport suspension but comfortably inside the global 1.5 s ceiling | v1 — pre-baseline |
| Washboard contact-loss | 2–8% of station transit time | Softer suspension tracks rough ground reasonably but isn't sport-tuned | v1 — pre-baseline |
| Hill grade | holds 25–30% grade, no stall/rollback | Modest FWD torque; typical family-hatch hill-holding capability | v1 — pre-baseline |

---

## Coupe — target bands

Reference basis: sports-coupe class on performance tires (class reference ≈0.95–1.05 g
skidpad, 0–100 ≈ 5–6 s directly). No deliberate game-feel deviation — Coupe is meant to
read as a "real" RWD sports car; its brief is faithfulness, not exaggeration.

| Maneuver | Target band | Reference basis / deviation note | Status |
|---|---|---|---|
| Launch 0–100 km/h | 5.0–6.0 s | Typical sports-coupe 0–100 time (class reference example) | v1 — pre-baseline |
| Top speed | 230–260 km/h | Typical performance-coupe top-end; torque curve and gearing support it | v1 — pre-baseline |
| Brake 100–0 m | 42–48 m | **Re-anchored** to bracket the accepted measured state (feel session 2026-07-12: braking accepted). Was 32–37 (v1); the shared 2-state ABS ceiling puts hatch and coupe together ≈47 m — feel verdict outranks the aspirational bands. Slip-servo ABS that could reach the old band is CLOSED-WONTFIX (see baseline-metrics.md). | v2 — feel session 2026-07-12 |
| Skidpad lateral g @ 20 m | 0.95–1.05 g | Typical sports-coupe skidpad rating on performance tires (class reference example) | v1 — pre-baseline |
| Slalom 18 m-gate, avg through-gate speed | 68–76 km/h, 0 strikes | Higher grip + quicker steering response than Hatch supports a faster pace | v1 — pre-baseline |
| Handbrake J-turn, 180° time | 1.4–2.0 s | RWD handbrake turns are crisp/fast — classic RWD trait, rear steps out cleanly under handbrake | v1 — pre-baseline |
| Lift-off oversteer, yaw overshoot | 20–35°, damped within 1.2 s, never past 220° | Pronounced but controllable RWD lift-off oversteer — the archetypal sports-car trait; must stay recoverable, not a spin | v1 — pre-baseline |
| Ramp-jump landing settle | < 1.0 s, 0 flips | Stiff sport dampers settle fast, well inside the global 1.5 s ceiling | v1 — pre-baseline |
| Washboard contact-loss | 0–5% | Performance suspension tracks rough ground well at speed | v1 — pre-baseline |
| Hill grade | holds 35–40% grade | High RWD torque; band assumes street-tire (not off-road) surface grip | v1 — pre-baseline |

---

## Kart — target bands

Reference basis and **deliberate deviation**: real sport/shifter karts, thanks to near-zero
CoM height and tiny mass, post absurd numbers on paper — 1.5 g+ skidpad, sub-2s to 60 mph —
that would read as twitchy and *uncatchable* if reproduced literally. This class is
deliberately tamed rather than maxed out (short gearing, proportional TC, a stability assist)
to make it driveable — without that, telemetry
had fingerprinted the kart living in permanent wheelspin (`rearK` up to 5.8 with RPM pegged
in gear 1 at only 43 km/h, "floaty rear always") and an unrecoverable lift-off "L-R flick
spin" (rear slip angle 31°→50°, yaw −103°/s, never settled). The fix — short gearing (1st
gear now tops ~34 km/h), proportional TC that holds slip near the grip peak instead of an
on/off cut, and a stability assist that damps yaw once rear slip exceeds ~6° — is why every
band below undershoots the real-world reference on purpose: **fun-first and catchable-first,
not lap-record-first.** Kart's real mass in `CarDefinition.cs` is 260 kg (heavier than the
brief's rough "~150 kg-class" note, because the Kenney kart model bundles a driver figure).

| Maneuver | Target band | Reference basis / deviation note | Status |
|---|---|---|---|
| Launch 0–50 km/h | 2.0–5.0 s *(revision: 0–100 band self-inconsistent)* | **Band replaced 2026-07-12:** the kart's top speed (55–70 km/h) sits below 100 km/h, so a "0–100 km/h" launch band was physically unreachable and read `zeroToHundredS=0` on the unmodified car (baseline-metrics.md flagged it). Measured on a **0–50 km/h** split instead (pilot `splitSpeedMs=13.89`), which still captures the tamed short-gearing launch character. Band set from measurement; **deliberately unhurried** — the tame caps usable power before wheelspin rather than spending the launch sideways. | v1 — revised |
| Top speed | 72–86 km/h (20–24 m/s) *(was 55–70)* | **Raised (feel session 2026-07-13):** "could be faster in general — it's a literal race car design". Telemetry proved the old ceiling was GEARING-capped (gear 4 pinned at redline 9000 holding 62 km/h), not power/grip/drag. Fixed with a 5th gear (1.1) leaving gears 1–4 + FinalDrive untouched so the launch character is byte-identical; measured 21.82 m/s (78.5 km/h) in gear 5. Still "low top speed, instant response" relative to the roster — just less kneecapped. | v2 — feel session 2026-07-13 |
| Brake, own-top-speed to 0 | 8–14 m | Kart's top-speed band (55–70 km/h) sits below the nominal 100 km/h test speed, so this maneuver is measured from Kart's actual top speed, not literally "100–0"; light weight + short suspension travel give very short stopping distances despite modest absolute brake torque | v1 — pre-baseline |
| Skidpad lateral g @ 20 m | 1.00–1.20 g | **Deliberately down** from the real shifter-kart norm of 1.5 g+ — low CoM/light weight still deliver the "glued down" kart feel, capped so the limit stays catchable rather than borderline-undriveable | v1 — pre-baseline |
| Slalom 18 m-gate, avg through-gate speed | 45–55 km/h, 0 strikes | Tiny footprint/wheelbase gives very quick direction changes; capped top speed limits absolute pace. **Yaw-rate sanity re-anchored ≤130 → ≤320 (slalom re-anchor, measured 2026-07-13):** the kart's measured twitchy-fishtail signature is warm 180.45 / cold-first-play 308.9 (prior history 273.9/260) — always over the never-anchored ≤130 v1 sanity. ≤320 brackets the full measured range so a cold-sample battery doesn't flap; the twitchy fishtail is accepted entry-fun kart character (entry fun, exit fixed in a later pass). The slalom PILOT was also hardened (SlalomManeuver yaw-rate steer-gain backoff) for cold/warm reproducibility — harness-only measurement infrastructure, **no CarDefinition dial touched**. | band v1; yaw-sanity **v2 — slalom re-anchor 2026-07-13** |
| Handbrake J-turn, 180° time | 1.0–1.6 s | Light RWD rotates almost instantly; band is fast but bounded — the "Tame the kart" stability assist is what keeps it from over-rotating past 220° | v1 — pre-baseline |
| Lift-off oversteer, yaw overshoot | ≤ 20°, damped within 0.8 s, never past 220° | Directly grounded in the "Tame the kart" fix (spec 5.2.3 stability assist ramping in above ~6° rear slip); this band is effectively a regression check that the tame still holds | v1 — pre-baseline |
| Ramp-jump landing settle | < 1.0 s, 0 flips | Light chassis settles fast time-wise even though it bounces more visibly (see washboard row / tire-bounce-quality heuristic below) | v1 — pre-baseline |
| Washboard contact-loss | 5–15% | **Deliberately up** from the other two classes — small wheels and short suspension travel make karts inherently bouncier over rough ground in reality too; this is the kart's tire-bounce signature, not a defect, as long as it stays under the no-resonance ceiling | v1 — pre-baseline |
| Hill grade | holds 15–20% grade only | Low torque + short gearing + tiny mass = weak hill climber; real karts aren't hill-climbers either, so no deliberate deviation here — this one's just a realistic limitation | v1 — pre-baseline |

---

## Pickup — target bands

*(Added 2026-07-12 with the pickup part-kit — `CarDefinitions.Pickup`, 1900 kg, RWD,
320 N·m low-rev, offroad tires, 0.24 m travel. Tuning rationale per field in
`docs/pickup-kit.md` §2.)*

Reference basis: full-size work pickup on all-terrain tires (F-150/Silverado-class:
≈0.70–0.78 g skidpad, 0–100 ≈ 7–9 s for modern V8s but 10–12 s for the work-truck
powertrains this class channels, 100–0 ≈ 43–48 m). Deliberate game-feel shaping: the
pickup is the roster's **hill-grade king and rough-ground cruiser** — strongest climber
by design (torque + RWD grade load-transfer both point that way), softest over washboard —
while paying for it with the longest braking, lowest lateral grip, and slowest slalom.
Every trait pair is a real-truck trade, so the class reads coherent rather than nerfed.

| Maneuver | Target band | Reference basis / deviation note | Status |
|---|---|---|---|
| Launch 0–100 km/h | 10.0–12.0 s | Work-truck powertrain: torquey but heavy (1900 kg) and geared for pull, not sprint; traction-limited RWD launch on offroad rubber | v1 — pre-baseline |
| Top speed | 140–165 km/h | Low 4700 rpm redline + offroad tires; gearing tops out ~185 km/h but power/drag land it here — trucks are not autobahn machines | v1 — pre-baseline |
| Brake 100–0 m | 40–46 m | Deliberately a bit longer than Hatch (36–42): mass + offroad-tire peak grip 0.90 vs street 1.00; brake torque scaled to mass×radius so it's tire-limited, not fade-y | v1 — pre-baseline |
| Skidpad lateral g @ 20 m | 0.70–0.80 g | Typical AT-tire truck skidpad; lateral curve peak 0.88 + higher CoM + load sensitivity land it below every car in the roster — in character | v1 — pre-baseline |
| Slalom 18 m-gate, avg through-gate speed | 48–58 km/h, 0 strikes | Slowest in roster: long 3.40 m wheelbase + slow 27° steering + soft springs = deliberate, bargey transitions | v1 — pre-baseline |
| Handbrake J-turn, 180° time | 2.0–2.8 s | RWD rotates, but high yaw inertia (long body, heavy) makes it slower than Coupe (1.4–2.0); still quicker than the FWD Hatch's ceiling | v1 — pre-baseline |
| Lift-off oversteer, yaw overshoot | ≤ 20°, damped within 1.3 s, never past 220° | Mild-to-moderate RWD lift-off response, softened by the gradual offroad lateral curve (wide peak→tail) — a slow push-then-rotate, not a snap | v1 — pre-baseline |
| Ramp-jump landing settle | < 1.5 s, 0 flips | Softest, longest-travel suspension in the roster settles slowest — sits AT the global ceiling on purpose; 0.24 m travel must absorb the hit without bottoming | v1 — pre-baseline |
| Washboard contact-loss | 1–6% | **Deliberately the roster's best** (with Coupe): long travel + soft 1.50 Hz springs are built for exactly this — the truck's rough-ground signature | v1 — pre-baseline |
| Hill grade | holds 40–45% grade, no stall/rollback | **THE signature strength — strongest in roster** (Coupe 35–40): 320 N·m through 3.8×3.9 gearing gives ~13 kN of gear-1 drive force vs ~8 kN needed at 45%, and uphill load transfer adds grip to the driven rear axle | v1 — pre-baseline |

---

## Feel heuristics as metrics

Every heuristic below is computed from telemetry fields the harness already commits to
recording: speed, yaw rate, lateral/longitudinal g, per-wheel slip ratio/slip angle/
load/contact (`IsGrounded`), and per-wheel suspension travel (`SuspensionLength`). None of
them require new instrumentation — they're re-derivations of the existing ring buffer +
per-run JSON report described in docs/testing-harness.md.

### 1. Catchability

**Setup:** handbrake-induced 30° yaw disturbance at 60 km/h, then a scripted counter-steer
input profile (fixed, deterministic — no reactive AI). **Metric:** time for
yaw rate to fall back under threshold X (deg/s) and stay there, measured from disturbance
onset, without total accumulated rotation exceeding 220° (the same ceiling as the `jturn`
maneuver's pass shape). **Telemetry:** yaw rate (already logged per-tick as `yaw
{value}deg/s` in `VehicleController`'s `[vp] tele` line; integrate for total rotation).

| Class | Yaw rate must fall under (X) | Within (Y) | Rationale |
|---|---|---|---|
| Hatch | 15 deg/s | 1.2 s | FWD, understeer-biased — less prone to divergent yaw to begin with, so the bar is a slower-but-safe settle |
| Coupe | 20 deg/s | 1.5 s | RWD with real power — allowed a longer window because the disturbance it's recovering from is more dynamic (matches its larger lift-off-oversteer band above) |
| Kart | 25 deg/s | 1.0 s | Low polar inertia = fast yaw dynamics both ways; must settle quickly since "catchable" is the entire point of the kart stability tame — this is effectively that fix's acceptance test |

### 2. Planted-ness

**Metric:** lateral-g rise time to 90% of steady-state value, measured on a step-steer
input (captured during the `skidpad` maneuver's entry transient, or a dedicated step-steer
probe within the same station). **Telemetry:** lateral g from the ring buffer (the telemetry
lists "lateral+long g" as a recorded channel).

| Class | Rise time to 90% steady-state lateral g | Rationale |
|---|---|---|
| Hatch | 0.35–0.55 s | Softer springs/more body roll = slower, less "planted" transient — expected, not a bug |
| Coupe | 0.20–0.35 s | Stiffer springs/dampers = quick load transfer, reads as planted |
| Kart | 0.10–0.20 s | Near-instant — low CoM and short wheelbase are the kart's signature "instant response" |

### 3. Tire bounce quality

**Metric:** two-part — (a) washboard contact-loss % (already a maneuver output, see class
tables above), and (b) post-jump suspension settle time: time for `SuspensionLength` on all
four wheels to re-enter and stay within a tolerance band of resting length after the `jump`
maneuver's landing. **Telemetry:** per-wheel `IsGrounded` (contact-loss %) and
`SuspensionLength` (settle time) — both already exposed on `VehicleWheel`.

| Class | Washboard contact-loss | Post-jump settle time | Rationale |
|---|---|---|---|
| Hatch | 2–8% | < 1.3 s | See class table above |
| Coupe | 0–5% | < 1.0 s | See class table above |
| Kart | 5–15% | < 1.0 s | Bouncier (small wheels/short travel) but settles fast because it's light — "bouncy but not sloppy" is the target character |

### 4. Launch character

**Metric:** allowed wheelspin duration band — time any driven wheel's `SlipRatio` stays
above the traction-control engagement threshold (~0.2, matching the proportional-TC curve
in `drive`'s `VehicleController.ApplyStabilityAssist`/TC code) during the `launch` maneuver.
Must stay under the global ceiling of 3 s regardless of class. **Telemetry:** per-wheel
`SlipRatio`, already the basis of the `rearK`/`frontK`-style tele fields.

| Class | Allowed wheelspin duration | Rationale |
|---|---|---|
| Hatch | 0.0–0.5 s | FWD, modest torque (150 N·m) relative to grip — should hook up almost immediately |
| Coupe | 0.3–1.2 s | RWD with 340 N·m — a brief chirp-then-hookup is in-character (mirrors the taxi's original tuning comment "near the rear tires' traction limit: chirps, then hooks up"), not a defect |
| Kart | 0.0–0.6 s | Tight band on purpose — this is the exact failure mode the kart tame fixed (pegged wheelspin in gear 1); v1 band re-asserts that the tame holds, doesn't reopen the door to it |

---

## How bands get revised

This doc is the input to the tuning iteration loop, not a one-time artifact:

1. **Sanity check against the measured baseline.** Once `docs/baseline-metrics.md` exists,
   every band above gets checked against the *measured, unmodified* roster before any tuning
   starts. A band that the untouched car already blows through in the wrong direction is a
   band-authoring error, not a physics bug — fix the band here, not the car.
2. **Iteration loop drives dial/code changes, not bands.** During tuning, `vp_test.py --all`
   diffs measured metrics against these bands. If a car is out of band, the fix is a dial or
   physics change (LSD, CoM/load-transfer, damper curves, assist tuning) —
   bands stay fixed while the car is adjusted to hit them, unless (3) applies.
3. **Feel feedback re-enters as an adjusted, provenance-tagged band — never an
   untracked vibe.** If a car is in-band but still "feels wrong," the
   resolution is an edit to the relevant row in this doc (new min/max + a one-line reason,
   same date-tag convention), followed by another
   iteration loop pass against the revised target. A band never gets silently loosened just
   because a car failed to hit it — that direction of change requires the same provenance
   tag and a stated reason.
4. **Status graduates.** Once a class's bands have survived a full iteration pass and the
   battery is green ×3 consecutive (the exit criterion) for that class, its rows'
   Status column updates from `v1 — pre-baseline` to `v1 — confirmed` (or a new `v2` block if
   the bands themselves changed materially). Do this edit in place; don't fork a second file.

---

## Liftoff / Washboard / Hillclimb / Figure8 — maneuver-spec provenance (2026-07-12)

Spec files now exist for all four (`specs/maneuvers/{liftoff,washboard,hillclimb,figure8}.json`)
so the exclusion at the top of this doc ("figure8 and crash are later assist-composition
concerns, out of scope for this doc") is narrowed for figure8: a band now exists, it is simply
unvalidated (no pilot profile exists yet to drive the maneuver, so nothing has run). `crash`
remains fully out of scope. Per `docs/testing-harness.md`, these four are **specs
authored, pilot profiles pending** — dry-run-valid, not live-run-provable, until the C#
`VehiclePilot` gains the four maneuver implementations. Every value below is
**v1 — pre-measurement**.

### Liftoff — target bands (reused verbatim from the per-class tables above)

The "Lift-off oversteer, yaw overshoot" row already existed in each class table and
is unchanged; restated here with the spec-authoring detail for traceability.

| Class | Yaw overshoot band | Damping window | Never past 220° | Rationale | Status |
|---|---|---|---|---|---|
| Hatch | ≤ 15° | 1.0 s | n/a (band already far under) | FWD mild lift-off tuck-in, not true oversteer | v1 — pre-measurement |
| Coupe | 20–35° | 1.2 s | yes | Archetypal controllable RWD lift-off oversteer | v1 — pre-measurement |
| Kart | ≤ 20° | 0.8 s | yes | Regression check on the "Tame the kart" fix | v1 — pre-measurement |
| Pickup | ≤ 20° | 1.3 s | yes | Softened slow push-then-rotate (gradual offroad lateral curve) | v1 — pre-measurement |

Station: `bankedcurve` (proving-grounds.md flags the liftoff→bankedcurve mapping itself as an
assumption pending a dedicated "high-speed bend" — see report / proving-grounds.md §"Station
table" footnote). Metric: `yawOvershootDeg` (peak yaw deviation from straight during the
disturbance, not the J-turn "past-180" reading of the same field name — see the schema/contract
flags below). The Catchability heuristic table's per-class yaw-rate/window values (Hatch 15°/1.2s,
Coupe 20°/1.5s, Kart 25°/1.0s — no Pickup row) supplied the `settleYawDeg`/`settleWindowS`
pilot params in the spec; Pickup's `settleYawDeg` (20°) is extrapolated from its own overshoot
ceiling since it has no Catchability-table row (added after that table was authored).

### Washboard — target bands (reused verbatim from the per-class tables above)

| Class | Contact-loss band | Settle ceiling (no-resonance proxy) | Rationale | Status |
|---|---|---|---|---|
| Hatch | 2–8% | < 1.3 s | Softer suspension tracks rough ground reasonably | v1 — pre-measurement |
| Coupe | 0–5% | < 1.0 s | Performance suspension tracks rough ground well at speed | v1 — pre-measurement |
| Kart | 5–15% | < 1.0 s | Deliberately up — small wheels/short travel is the kart's signature bounce, settles fast (light) | v1 — pre-measurement |
| Pickup | 1–6% | < 1.5 s | Deliberately the roster's best (with Coupe) — long travel + soft 1.50 Hz springs; settle ceiling extrapolated (no Pickup row in the Tire-bounce-quality table) | v1 — pre-measurement, settle ceiling flagged as extrapolation |

Station: `washboard` (unambiguous — matches both `proving-grounds.md` and `vp_test.py`'s
`STATIONS` set). Settle-ceiling values reuse the "Tire bounce quality" feel-heuristic table's
post-jump settle numbers, applied here per `testing-harness.md` §6.2's `settleS` row (which
already lists washboard as a consumer).

### Hillclimb — target bands (reused verbatim from the per-class tables above)

| Class | Grade band | Wheelspin ceiling | Rollback ceiling | Rationale | Status |
|---|---|---|---|---|---|
| Hatch | holds 25–30% | ≤ 3.0 s (global ceiling; no per-class band) | ≤ 0.5 m (placeholder) | Modest FWD torque, typical family-hatch hill-holding | v1 — pre-measurement |
| Coupe | holds 35–40% | ≤ 3.0 s | ≤ 0.5 m (placeholder) | High RWD torque, street-tire grip assumption | v1 — pre-measurement |
| Kart | holds 15–20% only | ≤ 3.0 s | ≤ 0.5 m (placeholder) | Low torque + short gearing + tiny mass; realistic limitation, no deliberate deviation | v1 — pre-measurement |
| Pickup | holds 40–45% | ≤ 3.0 s | ≤ 0.5 m (placeholder) | **THE signature strength** — strongest in roster by design (torque + RWD grade load-transfer) | v1 — pre-measurement |

**Flag — station geometry does not yet reach these grades.** `docs/proving-grounds.md`'s
`hillclimb` station (`TestTrack.cs`, drafted/uncompiled) currently builds only **4 discrete
segments at 5/10/15/20%**. Only the **Kart** band (15–20%) fits inside that ceiling; Hatch
(25–30%), Coupe (35–40%), and especially Pickup (40–45% — the roster's headline trait) all
exceed the built station's max segment. The spec entries author the true rated-grade `params`
regardless (so the JSON stays a faithful transcription of this doc, not a workaround), but they
cannot be driven live until either the station gains higher-grade segments or the bands are
revised down to match a capped station — a design decision, not something this
docs-only change can resolve. Also note: `vp_test.py`'s advisory `STATIONS` set spells this
station `hillgrade`, while `proving-grounds.md`'s authored `Stations` registry key is
`hillclimb` (same mismatch already present for `jturn`/`openpad` vs `jturnpad` — see
`specs/maneuvers/hillclimb.json`, which uses `hillclimb` to match the real authored registry
and triggers a harmless advisory dry-run note as a result).

### Figure8 — target bands (NEW — first bands authored for this maneuver)

Figure8 was excluded from this doc's original scope; these are its first bands, so
they get NEW rows, not a restatement. Grounded directly in **measured** skidpad
lateral-g reality (`docs/baseline-metrics.md`: Hatch 0.687 g, Coupe 0.680 g, Kart 0.770 g,
Pickup 0.655 g) with margin for figure8's tighter direction-change transitions — **deliberately
not** a restatement of the original v1 skidpad aspirational bands (0.80–1.20 g), per the
pre-read caution: authoring a band the current 0.80-friction-surface physics cannot reach is
the exact mistake already made once for skidpad and corrected.

| Class | Lateral-g avg band | Lateral-g peak ceiling (lobe-consistency proxy) | Assist pinned | Rationale | Status |
|---|---|---|---|---|---|
| Hatch | 0.55–0.80 g | ≤ 0.95 g | none (Casual default) | Anchored on measured skidpad 0.687 g; FWD keeps default assist per the §7.1 jturn precedent (only RWD cars get pinned) | v1 — pre-measurement |
| Coupe | 0.55–0.80 g | ≤ 0.95 g | Sport (assist:1) | Anchored on measured skidpad 0.680 g (near-identical to Hatch — the fixed-throttle skidpad-profile convergence artifact noted in baseline-metrics.md); RWD pinned per §7.1 extended to figure8 | v1 — pre-measurement |
| Kart | 0.65–0.90 g | ≤ 1.05 g | Sport (assist:1) | Anchored on measured skidpad 0.770 g, the roster's highest; RWD pinned per §7.1 extended to figure8 | v1 — pre-measurement |
| Pickup | 0.50–0.72 g | ≤ 0.87 g | Sport (assist:1) | Anchored on measured skidpad 0.655 g, the roster's lowest (still grip-saturated per baseline-metrics note 2, unlike the other three); RWD pinned per §7.1 extended to figure8 | v1 — pre-measurement |

Station: `skidpad` (figure8 "runs paired skidpad circles"). **Flag —
scope-limited assist coverage.** The figure8 pass shape is "clean under all 3 assist
levels," but `specs/maneuvers/figure8.json` authors **one row per car** (4 total, matching this
task's explicit "4 spec files × 4 cars each" scope), pinning each RWD car at Sport per the
§7.1-style extension rather than sweeping all three levels. A full 3-level sweep would be 12
entries (4 cars × Casual/Sport/Sim) — flagged as a known follow-up, not authored here.
"Lateral-g consistency between lobes" is approximated via `lateralGPeak` (peak shouldn't spike
far above the avg band's ceiling) because the frozen telemetry contract (`testing-harness.md`
§6.2) has no per-lobe metric — flagged below.

### Driftexit — target bands (NEW — feel session 2026-07-13)

The drift-exit recovery maneuver born from the kart drive-feel complaint ("entry is clean and
feels good; EXIT is very difficult — I get stuck sliding sideways and lose too much momentum").
Metric-first: the maneuver + metrics
landed FIRST, baselines were measured on the untouched cars, then the two candidate dial
changes went in and the acceptance bands were authored from the measured post-fix state.
Only the two complaint cars carry spec rows (hatch FWD holds the handbrake through its whole
rotation — a different maneuver; pickup was not complained about).

| Class | exitRecoveryS | speedRetention | peakSlipDeg ceiling | Rationale | Status |
|---|---|---|---|---|---|
| Kart | ≤ 1.0 s | ≥ 0.43 | ≤ 85° | Recovery aspiration MET (measured 0.54 s); retention band encodes the drift-catch gain (baseline 0.415 fails it, post-fix 0.451 passes) | v1 — feel session 2026-07-13 |
| Coupe | ≤ 1.0 s | ≥ 0.51 | ≤ 90° | Recovery aspiration MET (measured 0.66–0.70 s); retention band encodes the drift-catch gain (baseline 0.502 fails it, post-fix 0.525–0.528 passes) | v1 — feel session 2026-07-13 |

**The 0.75 retention aspiration is NOT reached and is honestly out of range of candidates
#2/#3:** in this tire model the slide's momentum scrub is set by the saturated friction
ellipse (μ·load regardless of rear slip ratio — the longitudinal curve's tail is flat past
~0.4 slip, so soft-lock caps −0.3 and −0.7 measured trajectory-identical to full lock).
Reaching ~0.75 on a 120°-yaw slide needs candidate #4 (lateral tail raise — stronger deep-slip
realignment force), which moves skidpad/slalom/jturn/figure8 and is therefore deferred to a
separate design decision (the lateral tail is not changed in this pass).

### Open schema/contract flags (from hand-validation, no editor available)

1. **Hillclimb station-geometry gap** — **RESOLVED 2026-07-13 (wave-2):** ladder extended to
   5-45% AND redesigned into a parallel fan (the serial layout measured an obstacle course —
   see `proving-grounds.md`); all four classes' rated grades are now drivable and were climbed
   in the first live battery (`baseline-metrics.md` "Wave-2 battery extension").
2. **`hillclimb` vs `hillgrade` station-name mismatch** — **RESOLVED 2026-07-13:**
   `vp_test.py`'s advisory `STATIONS` set now carries the authoritative registry keys too
   (`hillclimb`, `jturnpad`, `crashwall_reserved`); both spellings resolve live via the
   `StationCarRegistry` aliases (which needed no change).
3. **`testing-harness.md` §6.2 "maneuvers that read it" documentation lag** — **RESOLVED
   2026-07-13 (wave-2):** the §6.2 table now lists the new consumers, plus one contract
   addition made with the pilot profiles: washboard asserts `wheelContactLossPct` (per-wheel
   contact loss — this doc's feel-heuristic-3 provenance metric) because the full-airborne
   `contactLossPct` measured 0.0 on every car over the ridges. Band values unchanged.
4. **No dedicated "time-to-settle" telemetry field for liftoff.** `settleS` is documented only
   for `jump`/`washboard`. Liftoff's handling-targets "damped within Y s" language is therefore
   NOT directly asserted in `liftoff.json`; a generous `elapsedS` completion-sanity ceiling
   stands in its place, explicitly noted as an approximation in each entry.
5. **Verified against `python tools/vp_test.py --dry-run --all`:** all
   11 spec files, including the 4 new ones, return `0 invalid -> PASS`. The only output beyond
   plain `OK` lines is the advisory `hillclimb` station note in (2) above.
