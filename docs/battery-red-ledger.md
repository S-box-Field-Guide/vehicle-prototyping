# Battery Red Ledger

Pre-public-flip sweep of every failing (red) row in the maneuver battery. The rule this
document exists to satisfy: every red gets fixed or documented before the repo goes public,
so a reader always finds a stated reason and a proposed next step, never a silent failure.

No band value, spec threshold, or verdict logic is changed here. Any "re-anchor" action below
is a recommendation for the owner to approve, not something applied in this pass.

Provenance note: this repo's git history starts at the "Initial public release" commit
(`01d0078`), so anything measured before that point is only reachable through
`docs/baseline-metrics.md` prose, not through `git log` on the spec files themselves. Where a
red predates that commit, the citation below points at the doc section, not a commit hash.

---

## 1. J-turn grip-limit reds (coupe, pickup)

| | Coupe jturn | Pickup jturn |
|---|---|---|
| Band | 1.4-2.0 s | 2.0-2.8 s |
| Last measured | 3.499997 s | 2.9399974 s |
| Classification | measured-red | measured-red |
| Provenance | `docs/baseline-metrics.md` "RWD J-turn times" (honest-reds pass, 2026-07-15); reconfirmed bit-identical in the kart-reanchor leak check (2026-07-18) and the omega-anchor gate (2026-07-19, "jturn hatch and coupe BIT-IDENTICAL to baseline") | same honest-reds section (4.18 s pre-clamp); `docs/baseline-metrics.md` kart-reanchor leak check (2026-07-18, 2.9399974 s); omega-anchor gate (2026-07-19, "pickup on the same alias family, unchanged verdict") |
| Reason | RWD handbrake rotation is grip-limited: the accepted tuning-pass grip lift resists a scripted power-slide, so all three RWD cars rotate slower than their aspirational v1 bands. The 2026-07-18 omega clamp (`fix(physics): per-substep drive-side wheel omega clamp`) did not move this car's number | same root cause, but the omega clamp did measurably help: 4.18 s down to 2.94 s (about 30% faster), still above the 2.8 s ceiling |
| Proposed action | re-anchor to measured (~3.5 s) with owner sign-off, matching the kart jturn precedent, OR accept-and-document as a permanent RWD-character red | re-measure to confirm 2.94 s is stable, then either accept-and-document (already close to band) or a small re-anchor (~3.0 s ceiling) with owner sign-off |

**Correction to the assumed premise:** the task description for this ledger states both coupe
and pickup jturn "improved after the 2026-07-18 omega-clamp work." The documented data only
supports that for pickup. Coupe's jturn time is recorded bit-identical (3.4999... s) across
three separate write-ups spanning the pre-clamp and post-clamp measurements, so as far as this
repo's docs show, the clamp did not touch the coupe's number. Flagging this in case a coupe
measurement exists outside what's committed here.

Kart jturn, for context, is not red: it was re-anchored on 2026-07-18 (band 1.0-1.6 s -> 1.9-2.6
s, `specs/maneuvers/jturn.json`, commit `bb31c0b`) after the clamp made it measurably faster and
bit-repeatable (2.12-2.16 s), and now passes.

---

## 2. Kart brake band

| Metric | Band | Last measured | Classification |
|---|---|---|---|
| brakeDistanceM | 8.0-14.0 m | 17.97 m | measured-red, deferred |
| lockupTicks | <= 50 | 85 | measured-red, deferred |

Provenance: `specs/maneuvers/brake.json` kart row (band unchanged since v1); measured values
from `docs/baseline-metrics.md` "Feel round 2" (2026-07-13). The tuned-final ledger (2026-07-12)
recorded the same red at 18.1 m / 86 ticks.

Reason: the kart has never had a dedicated feel/tuning pass on its brake torque or ABS
constants. Car switching wasn't available until the 2026-07-12 feel session, and every session
since has prioritized other cars. The spec's own note says it plainly: "the kart hasn't had a
feel pass yet... so its red stays honest."

Proposed action: fix candidate, pending an owner-scheduled kart feel pass (brake torque / ABS
constants). Not a re-anchor candidate until that pass happens and produces a real accepted
number to anchor to.

---

## 3. Skidpad, all four cars ("benign drift band")

| Car | Band | Last measured | Verdict |
|---|---|---|---|
| Hatch | 0.80-0.90 g | 0.68708915 g | red |
| Coupe | 0.95-1.05 g | 0.678-0.686 g | red |
| Kart | 1.00-1.20 g | 0.7700-0.7722 g | red |
| Pickup | 0.70-0.80 g | 0.654-0.655 g | red (marginal, just under the floor) |

Provenance: `specs/maneuvers/skidpad.json`; measured values from `docs/baseline-metrics.md`
tuned-final table (2026-07-12) and the wave-2 / Feel-round-2 regression spot-checks
(2026-07-13), all reproducing within jitter.

Reason (documented, not guessed): a measurement-profile artifact, not a physics problem. The
skidpad pilot drives at a fixed 0.45 throttle. When tire grip was raised during tuning, every
car except the pickup stopped being grip-saturated at that fixed throttle, so lateral-g stayed
roughly flat while the underlying grip curve rose 30-50%. The pickup is the one car that stayed
saturated, which is why it alone moved (0.540 -> 0.655) and why it's only marginally red.

Proposed action: the real fix is a drive-to-saturation pilot profile (already identified and
staged, not yet built). Until that lands, this is either rerun-needed-after-fix or
accept-and-document with a re-anchor to the measured, jitter-stable state, both pending owner
word since re-anchoring is owner-gated.

---

## 4. Kart slalom maxSpeed ceiling flap

Band: `maxSpeedMs` between 12.50 and 15.28 m/s (`specs/maneuvers/slalom.json` kart row,
re-anchored 2026-07-18 alongside the pursuit-profile fix, commit range `f8964d5`/`08dcfbf`).

Documented measurements sit close under the ceiling with a small, deterministic margin:
15.054-15.056 m/s across two bit-identical battery runs (`docs/baseline-metrics.md`, "Kart
slalom: pursuit-profile re-anchor" section), described there as "a deterministic ~1.5% ceiling
margin." The same section documents the underlying mechanism for why some seeds land red: a
"low-throttle creep lets bad session seeds enter a gate at 16.2 m/s," over the row's own ceiling,
which is what triggers the fishtail/DNF failure mode the pursuit-profile change was built to
avoid.

Classification: owner-accepted. The 2026-07-18 date lines up with the v0.3.1 changelog entry
(`CHANGELOG.md` build 12, "the kart no longer locks into a turn at speed... threads a full
slalom cleanly"), i.e. the same release the task description says this was accepted for.

Reason: this is a marginally-stable closed-loop pursuit maneuver on a light, twitchy RWD car;
the ceiling sits close enough to the settled value that session-to-session context (not a real
regression) can flip a run red.

Proposed action: accept-and-document at the current band. A further fix candidate (tightening
the pursuit profile's determinism so no seed can approach the old excitation state) is a
harness-hardening item, not a band change, and isn't required to ship.

---

## 5. Driftexit: could not confirm as currently red

The task description for this row states kart speedRetention measures 0.263 and coupe measures
about 0.37, against bands of 0.43 and 0.51, with the owner having hand-tested and accepted the
feel despite the red.

That does not match anything in this repo. `specs/maneuvers/driftexit.json` and every mention of
driftexit in `docs/baseline-metrics.md` (introduced whole in the "Initial public release"
commit and never edited since) show both rows passing:

| Car | Band (speedRetention) | Documented measured value | Verdict |
|---|---|---|---|
| Kart | >= 0.43 | 0.451 | PASS |
| Coupe | >= 0.51 | 0.525-0.528 | PASS |

No commit, doc section, or spec note anywhere in this worktree records 0.263 or ~0.37 for
either car. I searched the full history of `driftexit.json` and every "drift" mention in
`baseline-metrics.md`; nothing matches.

Classification: unconfirmed / discrepancy, not a documented red at all as far as this repo
shows.

Proposed action: before this becomes a public-facing "accepted red," the owner should confirm
where the 0.263 / ~0.37 measurement came from (a run that was never committed here, a different
metric, a units mixup, or a stale memory of an older number). If a real post-omega-clamp
regression dropped these values, it needs a fresh measured entry in `baseline-metrics.md` before
it can be accepted-and-documented; right now there is nothing to document.

---

## 6. No-baseline reds: never re-run since the 2026-07-18 omega clamp

All of the following were last measured live on 2026-07-13 (the wave-2 battery extension), which
predates the omega-clamp physics fix (`7421f8b`, 2026-07-18) by five days. Only the kart's jturn
and slalom rows were explicitly re-measured and re-anchored after the clamp landed; nothing else
in this group has a post-clamp number in the repo.

The 2026-07-19 "omega-anchor fix battery gate" commit (`783fc88`) states a full battery ran via
`vp_test.py --all` and reports "GATE PASS... zero verdict flips... all FAILs are the pre-existing
documented reds, unchanged." That commit only cites hard numbers for jturn, brake, and slalom; it
references an `artifacts/battery-omega-fix-919c096/` folder for the rest that was never committed
to this repo. So there may already be a live confirmation that these rows are unchanged, but it
isn't citable from anything in this worktree.

### Liftoff (4 rows: hatch, coupe, kart, pickup)

| Car | Metric | Band | Measured (2026-07-13) | Reason |
|---|---|---|---|---|
| Hatch | elapsedS | <= 8.0 | 10.0 | completion-sanity ceiling authored before any profile existed; a standing-start car needs 6-8 s to reach entry speed alone |
| Coupe | elapsedS | <= 8.0 | 10.0 | same |
| Coupe | yawOvershootDeg | 20-35 | 10.56 | band is aspirational "archetypal RWD" character never actually measured; coupe's lift response is mild, matching its jturn finding |
| Kart | elapsedS | <= 8.0 | 8.34 | same completion-sanity issue |
| Kart | yawOvershootDeg | <= 20 | 64.76 | did not spin (spunOut false); open question whether the tame is weaker under the tuning-pass grip lift, or the metric over-counts benign path curvature for high-grip cars |
| Pickup | elapsedS | <= 8.0 | 10.0 | same completion-sanity issue |

Provenance: `docs/baseline-metrics.md` "Wave-2 battery extension," "Why the reds are red"
items 1-3.

### Washboard (3 rows: hatch, kart, pickup; coupe passes)

| Car | Band | Measured (2026-07-13) |
|---|---|---|
| Hatch | 2-8% | 0.0% |
| Kart | 5-15% | 3.29% |
| Pickup | 1-6% | 0.0% |

Reason: all three blow through the band in the good direction (less contact loss than
authored), which per the handling-targets revision rule is a band-authoring error, not a
physics bug. Long-travel suspensions simply track the 0.12 m ridges; the per-class *ordering*
the bands wanted (kart bounciest, others cleaner) is correct, just not the absolute floors.

### Hillclimb (3 rows: hatch, coupe, kart; pickup passes)

| Car | wheelspinS band | Measured (2026-07-13) |
|---|---|---|
| Hatch | <= 3.0 | 4.32 |
| Coupe | <= 3.0 | 4.84 |
| Kart | <= 3.0 | 4.46 |

Reason: metric-window mismatch. The 3 s ceiling was authored for launch character (a few
seconds of full throttle from a stop); a hillclimb run is 15-20 s of driving and the wheelspin
counter accumulates across the whole run, not just the launch. The pickup, climbing the
steepest grade, spins least (2.76 s) and passes, in the same torque-to-grip order as the rest
of the roster.

### Figure8 (3 rows: coupe at Casual / Sport / Sim)

| Assist | lateralGAvg band | Measured (2026-07-13) | elapsedS |
|---|---|---|---|
| Casual | 0.55-0.80 g | 0.34-0.35 g | 35.0 s DNF |
| Sport | 0.55-0.80 g | 0.34-0.35 g | 35.0 s DNF |
| Sim | 0.55-0.80 g | 0.34-0.35 g | 35.0 s DNF |

Reason: same fixed-0.45-throttle profile artifact as the skidpad reds. The coupe never reaches
grip saturation at that throttle, circles wide, and needs more than 35 s to close two 330-degree
lobes at any assist level. Hatch, kart, and pickup all pass 3/3.

### The un-locatable 14th item ("jump/pickup")

The task description lists this group as 14 rows total: liftoff x4, washboard x3, hillclimb x3,
figure8 x3, plus "jump/pickup." Counting the rows above gives 13. I could not find a red jump
row for pickup, or for any car: every documented jump measurement (tuned-final 2026-07-12,
audit-round-3 2026-07-13) shows all four cars passing, and the pickup's jump row was
specifically re-confirmed bit-identical in the 2026-07-13 Feel-round-2 regression check
("pickup topspeed/brake/skidpad/slalom/jump/washboard/hillclimb/liftoff bit-identical or
jitter-class"). If there's a specific jump measurement in mind, it isn't in this worktree's
docs; flagging as unconfirmed rather than inventing a 14th red.

---

## 7. Reds found beyond the task's list

Two more currently-red, currently-undocumented-as-accepted rows turned up while cross-checking
the launch spec against `docs/baseline-metrics.md`. Neither is mentioned in the task's known-red
list, and neither has an owner decision on record.

| Maneuver | Car | Metric | Band | Last measured | Reason (documented) |
|---|---|---|---|---|---|
| Launch | Hatch | wheelspinS | <= 0.5 s | 3.6599972 s | slip > 0.2 with a 2 m/s velocity floor counts low-speed contact-patch transients aggressively; TC retargeting to the curve peak halved it but a residual remains, "part metric artifact, part real" |
| Launch | Coupe | wheelspinS | <= 1.2 s | 3.0399978 s | same wheelspin-metric issue |
| Launch | Kart | wheelspinS | <= 0.6 s | 2.5999982 s | same wheelspin-metric issue |
| Launch | Coupe | zeroToHundredS | 5.0-6.0 s | 6.9999943 s | traction + open-diff + shift-cut limited; needs LSD/launch modeling (not built) or a band re-anchor |

All four values are reported bit-identical or near-identical across every battery run since the
tuned-final ledger (2026-07-12), including the most recent regression checks, so these aren't
flaky, just open. Proposed action for all four: fix candidate (metric refinement for the
wheelspin rows, LSD/launch modeling for the coupe time), or accept-and-document with a re-anchor,
pending owner word either way.

---

## Gate options

Two ways to close the public-flip gate given the 13 (or 14, pending confirmation) no-baseline
rows in section 6:

**Option A: master-baseline battery run.** Open the editor, run `python tools/vp_test.py --all`
end to end, and commit the resulting per-row numbers into `docs/baseline-metrics.md` the same
way every prior battery session has been recorded. This directly answers whether the omega
clamp changed liftoff, washboard, hillclimb, or figure8's numbers, the same way it was checked
for jturn and slalom. Cost: one live editor session; benefit: a citable, in-repo baseline for
every row, closing the gap the 2026-07-19 gate commit left open by not committing its artifacts
folder.

**Option B: scope the public gate to the with-baseline subset.** Ship with the gate defined over
the rows that already have a citable post-clamp measurement (everything except the 13/14 rows in
section 6), and carry those 13/14 forward as an explicitly documented, scoped exclusion (this
ledger entry stands in as that documentation) until Option A happens. This unblocks the flip
today without a live session, at the cost of shipping with an acknowledged coverage gap on four
maneuvers.

Recommendation for the owner to weigh: Option B is the faster path and this document already
satisfies "documented" for those rows: nothing here is unexplained, only unconfirmed against the
current physics build. Option A is the more complete fix and should happen at the next
convenient live-editor session regardless of which option ships first, since it also resolves
the driftexit discrepancy in section 5 and would close the "jump/pickup" ambiguity in section 6.
