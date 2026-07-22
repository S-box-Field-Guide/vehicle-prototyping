namespace VehicleProto;

// Game car roster. The CarDefinition type, the tuning-field schema, and the DriveLayout/BodyStyle/
// AssistLevel enums now come from the Vehicle Physics Kit (FieldGuide.VehiclePhysics, vendored under
// Libraries/); this file keeps ONLY the game's own tuned roster and its part-kit body manifests. It
// stays in namespace VehicleProto and keeps the name CarDefinitions so every existing call site
// (CarDefinitions.Hatch, .Coupe, .Kart, .Pickup) resolves here unchanged: a type in the current
// namespace wins over one imported through a global using, so VehicleProto.CarDefinitions is chosen
// over the kit's FieldGuide.VehiclePhysics.CarDefinitions demo roster with no ambiguity.
//
// The kit renamed CarDefinition's part-kit path field PartKitManifest -> BodyManifest (an opaque
// "which body to build" handle the kit itself never reads); the game's PartKitAssembler still reads
// it through the VehicleFactory.CustomBodyBuilder seam wired in PartKitFactory.

/// <summary>Car roster. Hatch is the default/first car; Kart and Coupe round out the roster.</summary>
public static class CarDefinitions
{
	/// <summary>
	/// The default/first roster car — an ORANGE hot hatch, spawned from the hatch_kit part kit.
	/// GEOMETRY follows the kit mesh: wheelbase 2.55 m, track 1.50 m, wheel r 0.30 m (hatch_kit spec)
	/// — the assembler audits def-vs-authored hub positions at 1 cm, so the wheels must sit in the
	/// authored arches. Tuning values are the measured hatch ledger (docs/baseline-metrics.md).
	/// If the kit ever fails to assemble, VehicleFactory spawns a primitive blockout and logs a clear
	/// error — there is no fused stand-in model (2026-07-15: fused fallback retired).
	/// </summary>
	public static CarDefinition Hatch => new()
	{
		BodyManifest = "models/vehicles/hatch_kit/manifest.json",
		Name = "Compact Hatch",
		Mass = 1150f,
		// belly-collider box; the kit's true extremities get their own part colliders
		BodySize = new Vector3( 3.9f, 1.75f, 1.45f ),
		Wheelbase = 2.55f,   // kit spec
		TrackWidth = 1.50f,  // kit spec
		WheelRadius = 0.30f, // kit spec
		Layout = DriveLayout.FWD,
		// +20% SPEED PASS (2026-07-21, owner feel: "all cars ~20% faster, both accel and top speed"):
		//   - PeakTorque 210 -> 252 (+20% on the current hand-bumped value; the 162->210 owner feel call
		//     stands, this stacks on top). EngineTorqueAt scales this flat across the rev band, so every
		//     gear pulls ~+20% harder (grip permitting), which is the acceleration half of the request.
		//   - RedlineRpm 6300 -> 7560 (+20%) is the TOP-SPEED half. RedlineWheelSpeed (Drivetrain.cs) sets
		//     the redline-equivalent top-gear wheel speed = RedlineRpm/(topGearRatio*FinalDrive)*WheelRadius;
		//     with no aero-drag term (VehicleWheel never integrates one) the terminal speed is gearing-set,
		//     so +20% redline = +20% gearing ceiling (hatch top-gear ceiling ~59.7 -> ~71.6 m/s).
		//   Why RedlineRpm and NOT FinalDrive for the top-speed lever: FinalDrive is inverse on wheel force,
		//   so lowering it for +20% top speed would erode the PeakTorque-driven +20% acceleration (they
		//   cancel) AND drop reverse top speed (reverse uses FinalDrive), fighting both goals. RedlineRpm
		//   raises the top-gear ceiling and the reverse redline together while leaving wheel force intact.
		//   ShiftUpRpm/ShiftDownRpm are DELIBERATELY left unchanged (hatch uses the kit defaults): the
		//   auto box shifts on GROUND-speed rpm, so a fixed shift point keeps the shift ladder at the same
		//   ground speeds (familiar launch/wheelspin character, no extra time held in the wheelspin-prone
		//   1st gear) while only the top gear (which never upshifts) extends to the new redline. The
		//   mild cost is intermediate gears now short-shift relative to redline (~74% vs ~92%), giving a
		//   slightly-under-20% mid-band torque bump (~+14-17%) that PeakTorque still carries net-positive.
		// Safety (unchanged, still verified in code):
		//   - per-substep drive-omega clamp (VehicleWheel.cs IntegrateWheelSpin): drive torque can never
		//     push a driven wheel past DriveOmegaCap (redline-equivalent for the current gear) within one
		//     substep; the backstop the kit's high-PeakTorque wobble-class fix targeted. It scales with
		//     the new redline, so the higher ceiling is enforced the same way.
		//   - smoothstep torque rolloff (VehicleWheel.cs, DriveRolloffOnset=0.90f): drive torque fades to
		//     zero as a wheel's own spin approaches 90% of DriveOmegaCap, so more torque can't camp a
		//     wheel at the cap and blow the slip ratio past the tire's tail.
		// TC sanity check: hatch is FWD, no Sport TC opt-in (Sport is raw "ABS only"). Casual TC targets
		// slip 0.14 by cutting THROTTLE proportionally against MEASURED slip, so +20% torque doesn't
		// overwhelm it; TC just clamps throttle harder to hold the same target.
		// Battery bands this pass will shift (owner sign-off required before re-anchoring; NOT touched here):
		//   - specs/maneuvers/topspeed.json (hatch): maxSpeedMs (47.22-54.17) shifts UP ~20%.
		//   - specs/maneuvers/launch.json (hatch): zeroToHundredS (8.0-10.0 s) IMPROVES (drops).
		//   - specs/maneuvers/launch.json (hatch): wheelspinS (<=0.5 s) roughly FLAT (TC target unchanged).
		PeakTorque = 252f,
		RedlineRpm = 7560f,
		// Real recorded car set: deep muscle idle crossfaded up to a revving high layer.
		EngineSoundEvent = "sounds/engine/engine_real_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_high.sound",
		// tuning iter 2a: longitudinal peak lifted 1.00→1.35 (folds in the 0.80 sim surface) so 100-0
		// can reach ~1.0 g for the 36-42 m band; iter 2b: BrakeTorque →4300 so torque overshoots the
		// grip cap and the ABS-cut phase still bites (run-3: the ABS duty cycle was the limiter).
		LongitudinalCurve = new TireCurve( 0.10f, 1.35f, 0.45f, 1.08f ),
		BrakeTorque = 4300f,
		// tuning iter 2b: lateral peak 1.00→1.30 puts skidpad mid-band (0.80-0.90 g); tail scaled with it.
		LateralCurve = new TireCurve( 0.14f, 1.30f, 0.55f, 1.04f ),
		// tuning iter 2b: the drift button — rear grip cut while handbrake held; also the FWD J-turn lever.
		HandbrakeGripScale = 0.55f,
		// Spin-recovery default (tested 2026-07-15 on the hatch, dialed 6→7: "7 feels pretty
		// good, definitely a big difference"). Tunable 0-12 via the Spin recovery dial.
		SpinRecoveryAssist = 7.0f,
		// steer-forgiveness pass 2026-07-17 (owner/tester feel): high-speed turn authority up
		// 8→9.5 (+19%) so mid-corner at speed needs less handbrake — MaxSteerAngle (low-speed lock,
		// inherited 32) and the 22 m/s blend point are UNCHANGED. Modest: still well under the
		// low-speed lock, keeps snap-oversteer protection at top speed.
		HighSpeedSteerAngle = 9.5f,
		SpringRate = 34000f,
		DamperRate = 2500f,
		// kit ORANGE so blockout-fallback/HUD chips match the kit body colour
		Tint = new Color( 0.93f, 0.42f, 0.03f ),
	};

	/// <summary>
	/// Full-size pickup, authored as a part kit
	/// from the start (16 parts — cab, separate bed, hinged tailgate, hood, doors, bumpers,
	/// grille fascia, mirrors, roll bar, wheels). Class brief: heaviest in roster, RWD,
	/// torquey low-rev engine, longer-travel softer suspension, offroad-leaning tires,
	/// high ride. Signature strength = hill grade (docs/handling-targets.md "Pickup" bands);
	/// full tuning rationale per field in docs/pickup-kit.md §2.
	/// GEOMETRY (wheelbase/track/wheel radius) must match the kit spec exactly — the
	/// assembler audits def-vs-authored hub positions at 1 cm (same rule as Hatch).
	/// </summary>
	public static CarDefinition Pickup => new()
	{
		Name = "Utility Pickup",
		BodyManifest = "models/vehicles/pickup_kit/manifest.json",
		Mass = 1900f,
		BodySize = new Vector3( 5.2f, 1.95f, 1.55f ), // belly-collider box kept INSIDE the true
		// extremities (bumper faces ±2.70, flares ±1.00) so the bumper/door PART colliders are
		// the outermost crash contact — the Stage-A feature (docs/part-kit-assembly.md §5)
		Wheelbase = 3.40f,           // kit spec — front axle +1.70, rear -1.70
		TrackWidth = 1.70f,          // kit spec
		RideHeight = 0.44f,          // high ride: the class signature (hatch 0.35)
		GroundClearance = 0.24f,
		CenterOfMassDrop = 0.15f,    // higher CoM than the cars = truck-like roll; track 1.70 keeps rollover margin over the 0.70-0.80 g skidpad band
		WheelRadius = 0.35f,         // kit spec — chunky 0.28 m-wide offroad profile
		WheelInertia = 2.4f,         // ~r² scaling + heavier tire vs hatch 1.2
		SuspensionTravel = 0.26f,    // longest in roster — washboard/offroad strength
		SpringRate = 42000f,         // 1.50 Hz ride freq (hatch 1.73) = softer RELATIVE to weight; static compression ~0.12 m leaves half the travel
		DamperRate = 3400f,          // ζ ≈ 0.38 — soft-truck settle without wallow
		// tuning iter 2a: offroad peak 0.90→1.25 (folds in 0.80 surface) for the 40-46 m brake band
		// (~0.95 g); keeps the long progressive slide shape (wide peak slip, high tail).
		LongitudinalCurve = new TireCurve( 0.14f, 1.25f, 0.60f, 1.05f ),
		// tuning iter 2b: lateral peak 0.88→1.22 puts skidpad mid-band (0.70-0.80 g); measured
		// 0.540 at 0.88 → scaled by mid-band ratio. Keeps the gradual push shape; still the
		// lowest lateral grip in the roster (class trait). Folds in the 0.80 surface.
		LateralCurve = new TireCurve( 0.15f, 1.22f, 0.60f, 1.06f ),
		LoadSensitivity = 0.07f,
		Layout = DriveLayout.RWD,
		// +20% speed pass 2026-07-21: PeakTorque 320->384 (+20% accel, keeps the signature torquey launch).
		PeakTorque = 384f,           // torquey: >2× hatch; gear-1 drive force = traction-limited launch, strong hills
		IdleRpm = 650f,
		// +20% speed pass 2026-07-21: RedlineRpm 3900->4680 (+20% top-gear top speed via the rev ceiling,
		// the same lever the earlier iter-2a change used in the other direction). The pickup is redline-
		// limited (it reaches its gearing ceiling: measured 43.1 m/s == computed ceiling), so this moves
		// its real top speed ~43 -> ~51.7 m/s. ShiftUpRpm 3500 left unchanged (shift ladder holds; only
		// top gear extends). The gear-1 launch torque (the hill-climb strength) is untouched.
		RedlineRpm = 4680f,          // lowest redline in roster; low-rev truck character (was 3900, +20% pass)
		EngineInertia = 0.5f,
		EngineBrakeTorque = 90f,     // strong engine braking downhill
		// Real recorded truck set: deeper truck idle + revving high layer; base pitch dropped so it
		// sits below the cars.
		EngineSoundEvent = "sounds/engine/engine_real_truck_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_truck_high.sound",
		EnginePitchBase = 0.85f,
		GearRatios = new[] { 3.8f, 2.3f, 1.5f, 1.1f, 0.85f },
		ReverseRatio = 3.8f,
		FinalDrive = 3.9f,
		ShiftUpRpm = 3500f,
		ShiftDownRpm = 1700f,
		BrakeTorque = 7000f,         // tuning iter 2a/2b: 4200→7000 — overshoot the grip cap so the ABS-cut phase still bites (run-3 duty-cycle analysis)
		BrakeBias = 0.65f,           // unladen bed = light rear axle
		HandbrakeTorque = 5000f,
		MaxSteerAngle = 27f,         // slow truck steering; long wheelbase stabilizes
		// steer-forgiveness pass 2026-07-17: 7→8 (+14%) — smallest bump in the roster so the pickup
		// stays the truckiest (lowest high-speed lock: 8 vs hatch 9.5, coupe 10). Low-speed lock 27
		// and the 22 m/s blend point unchanged.
		HighSpeedSteerAngle = 8f,
		SteerRateScale = 0.9f,
		// Reverse cap override removed 2026-07-21 (+reverse speed pass): inherits the new 20 m/s kit
		// default. The pickup's tall reverse gear (3.8) + low redline (4680) self-limit it to ~11.6 m/s
		// (~26 mph) reverse, so it stays the roster's slowest reverse by gearing character, not by the
		// old artificial ~9 mph clamp.
		// tuning iter 2b drift button: deepest rear cut in the roster — 1900 kg + 3.4 m wheelbase
		// never broke loose at 1.0 (J-turn no-180 through runs 1-3); the truck needs real help.
		HandbrakeGripScale = 0.45f,
		SpinRecoveryAssist = 7.0f, // spin-recovery default (tested 2026-07-15 on hatch, 6→7; other cars inherit as a starting point); tunable dial
		// Sport-mode posture (owner call 2026-07-21) — same exposure as the coupe: torquey 320 N-m RWD
		// truck lights the rears to redline in raw Sport. Reduced-authority Sport TC + yaw damp; slightly
		// tighter TC than the coupe (heavier, less playful). LIVE-UNVERIFIED; owner to feel/tune.
		SportTcSlipTarget = 0.30f,
		SportStabilityScale = 0.5f,
		DefaultAssists = AssistLevel.Casual,
		Tint = new Color( 0.55f, 0.13f, 0.11f ), // matches kit body_red
	};

	public static CarDefinition Kart => new()
	{
		// Custom part-kit art. If the kit fails to assemble the factory spawns a primitive blockout
		// kart (BuildKartBody) and logs a clear error — there is no fused stand-in model.
		BodyManifest = "models/vehicles/kart_kit/manifest.json",
		Name = "Go-Kart",
		Style = BodyStyle.Kart,
		// Kit kart has an EMPTY bucket seat: the engine citizen seats via the factory
		// AddDriver path at the manifest's driver_seat_author_m sit point (brief §3).
		HasDriver = true,
		Mass = 260f,
		BodySize = new Vector3( 1.9f, 1.15f, 0.30f ),
		Wheelbase = 1.55f,
		TrackWidth = 1.14f,
		RideHeight = 0.17f,
		GroundClearance = 0.08f,
		CenterOfMassDrop = 0.02f, // tiny chassis: a deep drop puts CoM below the wheels
		WheelRadius = 0.16f,
		WheelInertia = 0.18f,
		SuspensionTravel = 0.14f,
		SpringRate = 24000f,
		DamperRate = 1600f,
		Layout = DriveLayout.RWD,
		// +20% speed pass 2026-07-21: PeakTorque 52->62.4 (+20% accel), RedlineRpm 9000->10800 (+20% top
		// speed; kart top-gear ceiling ~21.8 -> ~26.1 m/s, extending the "literal race car" character the
		// owner asked for). FinalDrive/GearRatios/ShiftUp unchanged, so launch/wheelspin behavior holds.
		PeakTorque = 62.4f, // punchy launch, still shifts out of wheelspin quickly
		IdleRpm = 1400f,
		RedlineRpm = 10800f,
		EngineInertia = 0.05f,
		EngineBrakeTorque = 8f,
		// The kart keeps the buzzier high loop (the v1 high-pitched character "could suit the
		// kart"); base pitch >1 preserves its whine, while the narrowed band tames the redline chipmunk.
		EngineSoundEvent = "sounds/engine/engine_b_sport_purr.sound",
		EnginePitchBase = 1.2f,
		// short kart gearing: telemetry showed gear 1 topping ~50 km/h = permanent wheelspin.
		// tuning iter 2a: FinalDrive 5.0→6.3 pulls top speed 21.6→~17 m/s into the (then) 15.3-19.4 band
		// while keeping the high-rev kart character (redline unchanged AT THAT TIME; the +20% speed pass
		// 2026-07-21 later raised redline 9000->10800, lifting the top-gear ceiling to ~26 m/s).
		// Feel session 2026-07-13: "could be faster in general — it's a
		// literal race car design". Telemetry showed the ceiling was GEARING-capped (gear 4 pinned at
		// redline 9000 holding 62 km/h), not power/grip/drag. Added a taller 5th gear (1.1) leaving
		// gears 1-4 + FinalDrive UNTOUCHED, so launch/wheelspin behavior is byte-identical and only the
		// top end extends (decouples the two bands, work-order candidate #1).
		GearRatios = new[] { 3.4f, 2.4f, 1.8f, 1.4f, 1.1f },
		ReverseRatio = 3.4f,
		FinalDrive = 6.3f,
		ShiftUpRpm = 8000f,
		ShiftDownRpm = 4000f,
		// tuning iter 2a: BrakeTorque 700→560 (baseline locked hard, 96 lockup ticks) and the raised
		// longitudinal grip below shortens the stop; ABS still modulates.
		BrakeTorque = 560f,
		HandbrakeTorque = 900f,
		MaxSteerAngle = 31f, // 38 deg turned the fronts into brakes; 28 felt too tight
		HighSpeedSteerAngle = 9f, // telemetry: rear alpha overshot front at 78 km/h = high-speed weave
		// sticky kart slicks: peak 1.15→1.55 (folds in 0.80 surface) — the 8-14 m brake band from
		// 61 km/h needs ~1.1-1.3 g, unreachable with street-grade coefficients on a 0.80 surface.
		LongitudinalCurve = new TireCurve( 0.09f, 1.55f, 0.40f, 1.24f ),
		// tuning iter 2b: lateral peak 1.15→1.66 puts skidpad mid-band (1.00-1.20 g "glued down");
		// measured 0.762 at 1.15 → scaled by mid-band ratio. Folds in the 0.80 surface.
		LateralCurve = new TireCurve( 0.12f, 1.66f, 0.45f, 1.32f ),
		// tuning iter 2b ABS dials (D1): earlier release for the lockup-prone kart (70-96 lockup
		// ticks at the old 0.3/0.55 consts); softer cut keeps decel while stopping the locks.
		AbsSlipThreshold = 0.20f,
		// tuning iter 2b drift button: mild rear cut — the light kart already rotates; keeps the
		// J-turn quick without making the tame pointless.
		HandbrakeGripScale = 0.70f,
		SpinRecoveryAssist = 7.0f, // spin-recovery default (tested 2026-07-15 on hatch, 6→7; other cars inherit as a starting point); tunable dial
		// Sport-mode posture (owner call 2026-07-21) — same exposure as the coupe: light RWD kart spins
		// its rears freely in raw Sport. Loosest Sport TC in the roster + gentle yaw damp (the kart already
		// rotates easily, keep it playful). LIVE-UNVERIFIED; owner to feel/tune.
		SportTcSlipTarget = 0.40f,
		SportStabilityScale = 0.45f,
		// Feel session 2026-07-13: drift-exit soft-lock. Baseline (full lock)
		// measured driftexit speedRetention 0.415, peakSlip 77.6° — the "lose too much momentum"
		// complaint. Cap the handbrake-induced rear slip so the rears keep rotating mid-slide.
		HandbrakeSlipCap = -0.7f,
		// Recumbent kart driver pose (feel session 2026-07-13): a kart
		// driver reclines with legs extended forward to the pedals, NOT perched chair-upright with
		// knees at the wheel. sit=5 is a ground/reclined variant; offset + driver_seat_author_m tuned
		// in-engine so the legs reach forward without clipping the column/pods.
		DriverSit = 4,
		DriverSitOffsetHeight = 0f,
		DefaultAssists = AssistLevel.Casual, // default fun car: keep it catchable
		// Every roster car gets its own signature colour; kart = ACID GREEN (kit body_acid).
		Tint = new Color( 0.58f, 0.83f, 0.07f ),
	};

	public static CarDefinition Coupe => new()
	{
		// Custom part-kit art: the coupe runs its wedge part kit. Kit geometry authored to THIS def's
		// wheelbase/track/wheel radius — physics untouched (visual program only). If the kit fails to
		// assemble the factory spawns a primitive blockout and logs a clear error.
		BodyManifest = "models/vehicles/coupe_kit/manifest.json",
		Name = "Sports Coupe",
		Mass = 1420f,
		BodySize = new Vector3( 4.4f, 1.85f, 1.25f ),
		Wheelbase = 2.7f,
		TrackWidth = 1.60f,
		Layout = DriveLayout.RWD,
		// +20% speed pass 2026-07-21: PeakTorque 340->408 (+20% accel), RedlineRpm 7200->8640 (+20% top-gear
		// top speed). ShiftUpRpm 6600 left unchanged (shift ladder holds; only top gear extends to redline).
		PeakTorque = 408f,
		RedlineRpm = 8640f,
		ShiftUpRpm = 6600f,
		// Real recorded car set (shared with the hatch): deep muscle idle crossfaded up to a
		// revving high layer; base pitch neutral so it sweeps the band cleanly.
		EngineSoundEvent = "sounds/engine/engine_real_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_high.sound",
		EnginePitchBase = 1.0f,
		// tuning iter 2a: longitudinal peak 1.15→1.50 (perf tires; folds in 0.80 surface) for the
		// 32-37 m brake band (~1.1 g); iter 2b: BrakeTorque →6200 (torque must overshoot the
		// grip cap so the ABS-cut phase still bites — run-3 duty-cycle analysis).
		LongitudinalCurve = new TireCurve( 0.09f, 1.50f, 0.40f, 1.20f ),
		// tuning iter 2b: lateral peak 1.12→1.69 puts skidpad mid-band (0.95-1.05 g); measured
		// 0.664 g at 1.12 → scaled by mid-band ratio. Folds in the 0.80 surface.
		LateralCurve = new TireCurve( 0.13f, 1.69f, 0.50f, 1.36f ),
		// tuning iter 2b drift button: rears break loose under handbrake (J-turn initiation; the
		// raised longitudinal grip otherwise hooks up under the power-slide — run-3 regression).
		HandbrakeGripScale = 0.55f,
		// Feel session 2026-07-13: drift-exit soft-lock (class-generic complaint;
		// the captured drift-exit telemetry was the coupe's). Baseline (full lock) measured driftexit
		// speedRetention 0.502, peakSlip 82.2°. Cap the handbrake-induced rear slip so the rears keep
		// rotating mid-slide and retain lateral authority for the exit.
		HandbrakeSlipCap = -0.7f,
		SpringRate = 46000f,
		DamperRate = 3600f,
		BrakeTorque = 6200f,
		WheelRadius = 0.33f,
		SpinRecoveryAssist = 7.0f, // spin-recovery default (tested 2026-07-15 on hatch, 6→7; other cars inherit as a starting point); tunable dial
		// Sport-mode posture (owner call 2026-07-21: "Sport mode spins out all over the place"). Sport ran
		// with NO traction control and NO yaw damping (both Casual-only), so full throttle spun the 340 N-m
		// RWD rears to redline (telemetry rearK ~11 at 10 km/h in gear 2) and the counter-steer pendulum
		// went divergent. These add a reduced-authority Sport TC + yaw damp that keep the tail lively and
		// drift-capable but recoverable. Starting values — LIVE-UNVERIFIED; owner to feel/tune.
		SportTcSlipTarget = 0.35f,
		SportStabilityScale = 0.5f,
		MaxSteerAngle = 30f,
		// steer-forgiveness pass 2026-07-17: sportiest car gets the most high-speed turn-in — 8→10
		// (+25%, top of the roster: pickup 8 < hatch 9.5 < coupe 10). Low-speed lock 30 and the
		// 22 m/s blend point unchanged; 10 is still a third of the low-speed lock, no top-speed twitch.
		HighSpeedSteerAngle = 10f,
		// Colour directive: bright SIGNAL red (kit body_signal_red) — clearly
		// distinct from the pickup's dark brick 0.55,0.13,0.11.
		Tint = new Color( 0.80f, 0.05f, 0.07f ),
	};
}
