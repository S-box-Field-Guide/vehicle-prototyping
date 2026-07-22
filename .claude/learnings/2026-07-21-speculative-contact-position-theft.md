---
date: 2026-07-21
problem: Multi-session "hitch going off ramps" was the physics body being position-clamped by event-silent speculative contacts against segmented static collider seams
tags: [physics, colliders, telemetry, ramps, sbox]
---

## Symptom
Owner reported a hitch crossing ramps that survived five model-driven fix rounds (geometry,
wheel visuals, drivetrain, air assists, wheel wind-down). Reframed late as VISUAL: "speed feels
maintained, launch is fine, but the car model looks stuck on the ramp." Worse at high refresh;
fps_max 50 masked it partially.

## Root cause / key insight
The ramp face collider was a chain of ~30 overlapping convex BoxColliders (one per arc segment).
On the face, the physics body's per-tick displacement ran at 11-31% of velocity*dt, in stalls
spatially locked to the 0.7 m segment pitch, while Rigidbody.Velocity stayed perfectly smooth and
NO collision events fired on any car collider. That triple signature (position stolen, velocity
untouched, event silence) is speculative contact clamping: the engine's narrowphase creates
predictive constraints against the segment boxes' buried INTERNAL faces and clamps integration
against them without ever producing a touching manifold. Rendering interpolated the ratchet
faithfully; the "visual" hitch was real physical stutter.

## Approach that worked
1. Instrument before modeling: a flight recorder logging every fixed tick AND every render frame
   into RAM, dumped as CSV. Offline analysis (Python) against the recorded truth, not feel.
2. Integration audit: compare per-tick displacement against velocity*dt per surface. Flat ground
   integrated exactly (1 mm), the face did not: that single contrast eliminated every
   rendering/camera/interpolation hypothesis at once and localized the bug inside the physics
   step.
3. Discriminating columns, added in a v2 pass: velocity components (is velocity really smooth?),
   PhysicsBody.Position alongside GameObject position (engine write-back layer vs solver), and a
   collision-event probe (who touches?). Each column was chosen to split the remaining hypothesis
   space in half.
4. Controlled A/B: built a twin kicker identical in profile with ONE closed AddCollisionMesh
   collider, placed beside the segmented original, owner drove both with the recorder armed.
   Mesh: integration ratio 1.00. Boxes: 0.31. Verdict in a single run; fix = flip the default
   collider architecture.

## Dead ends ruled out
- Render interpolation / camera / WheelVisual phase: engine source read (sbox-public) showed the
  fixed-tick buffer pipeline is mathematically smooth and per-channel; falsified anyway by the
  flat-vs-face contrast.
- Chassis/part collider geometric contact: clearance arithmetic (hatch belly 0.097 m above face
  at climb compression) said nothing touches; the event probe agreed. NOTE: a false alarm came
  from parsing manifest bounds with a defaulted nonexistent field; the real fields are
  local_bounds_min_m/max_m.
- Kit teleports / pilot position writes / velocity-writing assists: grep showed none active on
  the face (wall-glance gates on wall-normal contacts that never fired).
- "Ghost contacts are dead because the probe saw zero events": WRONG inference, nearly killed the
  correct theory. Speculative constraints never raise collision events. An event probe can only
  falsify manifold-contact hypotheses, not speculative ones.

## Rule for next time
Any vehicle/character stutter on SEGMENTED static geometry (chained boxes, tile seams, prism
strips): first run an integration audit (per-tick displacement vs velocity*dt, per surface type).
If displacement lags velocity with no collision events, assume speculative clamping against
internal faces and A/B the same geometry as ONE closed collision mesh before touching gameplay
code. Never build drivable curved surfaces from overlapping convex primitives when a closed mesh
is available; internal faces near the drive surface are a solver hazard even when nothing can
geometrically touch them.

## Pointers
- Fix: Code/World/RampKicker.cs (ColliderMode, SolidMesh default) commit 9d216be
- Instrument: Code/Game/RampTraceRecorder.cs (v2), Code/Game/RampContactProbe.cs commit 8d4a9e5
- A/B twin (removed after verdict): commit c3ca798; verdict capture ramptrace2-211713.csv
- Engine facts: sbox-public GameTransform.Interpolation.cs, ScenePhysicsSystem.cs,
  CollisionEventSystem.cs (events dispatch to self+descendants of the body root)
