namespace VehicleProto;

public enum DriveLayout
{
	FWD,
	RWD,
	AWD
}

public enum BodyStyle
{
	Box,  // sedan/hatch blockout: body + cabin
	Kart  // flat deck + seat back + citizen driver
}

public enum AssistLevel
{
	Casual, // ABS + traction control
	Sport,  // ABS only
	Sim     // nothing
}

/// <summary>
/// Complete tuning spec for one car (spec 5.3). Plain class for now, instantiated from
/// <see cref="CarDefinitions"/>; promote to a [GameResource] .car asset once the editor
/// workflow is in play so designers can tune without recompiling.
/// All values SI: meters, kg, N, N-m, N/m.
/// </summary>
public class CarDefinition
{
	public string Name { get; set; } = "Car";
	public BodyStyle Style { get; set; } = BodyStyle.Box;
	public bool HasDriver { get; set; }

	/// <summary>Optional part-kit manifest path (mounted-fs relative, e.g.
	/// "models/vehicles/hatch_kit/manifest.json"). When set, VehicleFactory assembles the body
	/// from separate part GameObjects instead of a primitive blockout; null keeps
	/// the blockout path byte-for-byte unchanged. Physics is def-driven either way.</summary>
	public string PartKitManifest { get; set; }

	// Chassis
	public float Mass { get; set; } = 1200f;
	public Vector3 BodySize { get; set; } = new( 4.0f, 1.8f, 1.3f ); // length, width, height (m)
	public float Wheelbase { get; set; } = 2.55f;
	public float TrackWidth { get; set; } = 1.55f;
	public float RideHeight { get; set; } = 0.35f; // chassis center above wheel attach plane (+2 cm clearance pass 2026-07-14)
	public float GroundClearance { get; set; } = 0.14f; // collider bottom above ground at rest
	public float CenterOfMassDrop { get; set; } = 0.20f; // CoM below chassis center; must stay above wheel plane

	// Wheels & suspension (per wheel)
	public float WheelRadius { get; set; } = 0.31f;
	public float WheelInertia { get; set; } = 1.2f; // kg m^2
	public float SuspensionTravel { get; set; } = 0.20f;
	public float SpringRate { get; set; } = 38000f;
	public float DamperRate { get; set; } = 2800f;

	// Tires
	public TireCurve LongitudinalCurve { get; set; } = TireCurve.Street;
	public TireCurve LateralCurve { get; set; } = new( 0.14f, 1.00f, 0.55f, 0.80f ); // radians: peak ~8 deg
	public float LoadSensitivity { get; set; } = 0.06f;

	// Drivetrain
	public DriveLayout Layout { get; set; } = DriveLayout.FWD;
	public float PeakTorque { get; set; } = 150f;
	public float IdleRpm { get; set; } = 900f;
	public float RedlineRpm { get; set; } = 6500f;
	public float EngineInertia { get; set; } = 0.25f; // kg m^2 at crank
	public float EngineBrakeTorque { get; set; } = 40f;
	public float[] GearRatios { get; set; } = { 3.6f, 2.1f, 1.4f, 1.05f, 0.85f };
	public float ReverseRatio { get; set; } = 3.4f;
	public float FinalDrive { get; set; } = 3.9f;
	public float ShiftUpRpm { get; set; } = 5800f;
	public float ShiftDownRpm { get; set; } = 2200f;

	// Engine audio (read by EngineAudio). Per-class dials so a small buzzy kart and a deep
	// sedan/truck share one code path:
	//   EngineSoundEvent     — the LOW-RPM loop this class plays (idle/low, the deep layer).
	//   EngineSoundEventHigh — the HIGH-RPM loop, crossfaded in as revs climb. When null/empty the
	//     class runs the legacy SINGLE-loop model (one handle, wide pitch sweep) — the kart keeps
	//     this so its single synth purr is unchanged. When set, EngineAudio blends the two recorded
	//     layers by RPM, and each layer only pitch-shifts a narrow band around its recorded pitch
	//     (no far stretch = no screech), which is the point of the layered model.
	//   EnginePitchBase      — multiplies the pitch of whichever model runs, so a class reads higher
	//     or lower overall (kart >1 stays buzzy, truck <1 sits deep).
	public string EngineSoundEvent { get; set; } = "sounds/engine/engine_real_low.sound";
	public string EngineSoundEventHigh { get; set; }
	public float EnginePitchBase { get; set; } = 1f;

	// Brakes
	public float BrakeTorque { get; set; } = 2400f; // total, split by bias
	public float BrakeBias { get; set; } = 0.62f;   // fraction to front
	public float HandbrakeTorque { get; set; } = 3000f; // rear wheels only

	// ABS dials (per-car; promoted from VehicleController consts
	// 0.3/0.55 because run-3 telemetry showed the ABS duty cycle, not tire grip, was the brake
	// limiter, and per-car ABS modulation is a legitimate class trait: the locking kart needs a
	// different release than the truck). Active in Casual + Sport.
	public float AbsSlipThreshold { get; set; } = 0.25f; // release when SlipRatio < -this under braking
	public float AbsReleaseFactor { get; set; } = 0.70f; // brake torque multiplier while released

	// Drift-exit soft-lock (feel session 2026-07-13): cap the
	// handbrake-INDUCED rear slip ratio. Telemetry of the drift-exit complaint showed the handbrake
	// clamps rearK to -1.00 (full lock) for the whole slide, so the rears never rotate and lateral
	// authority collapses into the tail of the friction curve (60°+ slip angles, "stuck sliding
	// sideways"). When a cap is active and a rear wheel is already sliding PAST it, its handbrake
	// torque is withheld that substep (ABS-style duty cycle) so the rear spins back up toward the cap
	// and keeps some rotation — hence some lateral bite — mid-slide. Default -1.0 = NO effective cap
	// (byte-identical full-lock behavior); only the two complaint cars (kart, coupe) soften it.
	// MEASURED TRUTH (driftexit battery 2026-07-13): the cap ENGAGES (tele rearK -0.72..-0.91 vs
	// -1.00 locked) but is trajectory-NEUTRAL in this tire model — any |slip| past the longitudinal
	// tail start (~0.4) evaluates to the same flat tail force and the friction ellipse stays
	// saturated, so caps -0.3 and -0.7 measured identical to full lock (retention Δ < 0.01). Kept
	// for the wheel-rotation state itself; the metric-moving exit fix is the drift-catch assist
	// (candidate #3, VehicleController.DriftCatchFactor).
	public float HandbrakeSlipCap { get; set; } = -1.0f;

	// Steering
	public float MaxSteerAngle { get; set; } = 32f; // degrees at standstill
	public float HighSpeedSteerAngle { get; set; } = 8f; // degrees at/above 30 m/s

	// Arcade feel dials (defaults = the original sim-leaning behavior)
	public float SteerRateScale { get; set; } = 1f;    // multiplies how fast steering ramps
	public float ReverseSpeedCap { get; set; } = 4.5f; // m/s before reverse throttle cuts
	public float LaunchBoost { get; set; } = 1f;       // torque multiplier at standstill, fades out by ~54 km/h
	public float BrakeAssist { get; set; } = 0f;       // extra chassis-level decel while braking (m/s²)
	// Spin-recovery assist (feel session 2026-07-15): after a handbrake spin the car keeps
	// rolling BACKWARDS (old travel direction) while the player holds throttle the new way — BrakeAssist
	// can't help there (forward throttle sets Brake=0), so nothing arcade-level arrests the stale
	// velocity. This is extra chassis-level decel along -velocity, applied ONLY when input throttle
	// opposes the ground velocity along the car's facing, fading out as the car rotates to face its
	// motion (VehicleController.ApplySpinRecoveryAssist). Same m/s² unit + never-reverse-within-a-step
	// cap as BrakeAssist; gated Assists != Sim. 0 disables. NOTE: BrakeAssist is 0 across the whole
	// roster today, so this ships at a nonzero player-tunable starting value rather than mirroring it —
	// the feature has to be ON for the drive-test the request asked for (see docs/baseline-metrics.md).
	public float SpinRecoveryAssist { get; set; } = 0f; // extra chassis decel killing stale opposing velocity (m/s²)
	public float HandbrakeGripScale { get; set; } = 1f; // rear grip multiplier while handbrake held (<1 = drift button)

	// Wall-glance forgiveness assist (feel session 2026-07-12: "hit a wall at an angle —
	// especially mid-drift — recenter parallel and preserve some velocity instead of dead-stopping").
	// An ASSIST, not a physics rewrite (VehicleController.ApplyWallGlanceAssist): a sustained
	// near-horizontal chassis contact while moving re-projects velocity along the wall tangent and
	// gently yaws the heading to match, scaled by incidence. Active only when Assists != Sim (Sim =
	// the accepted raw baseline). Head-on hits (incidence >= WallGlanceHeadOnDeg) get NO assist —
	// frontal crashes must stay hard stops or the future destruction program is gutted. Data-shaped
	// so a future Sim/Arcade preset layer can scale these per profile.
	public bool WallGlanceAssist { get; set; } = true;
	public float WallScrubFactor { get; set; } = 0.75f;    // fraction of speed kept along the wall tangent on a shallow graze
	public float WallGlanceShallowDeg { get; set; } = 35f; // at/below this velocity-vs-wall incidence: full assist
	public float WallGlanceHeadOnDeg { get; set; } = 60f;  // at/above this incidence: no assist (hard stop preserved)
	public float WallAlignStrength { get; set; } = 6f;     // yaw-align rate toward the wall tangent (per second)

	// Driver seated pose (citizen animgraph; sit enum: 0 none, 1-3 chair poses, 4-5 ground poses).
	// Defaults = the original upright chair pose the blockout driver path always used. Made
	// per-car (feel session 2026-07-13) so a recumbent kart driver — legs
	// extended forward to the pedals — can be authored without disturbing any upright-seated car.
	public int DriverSit { get; set; } = 1;
	public float DriverSitOffsetHeight { get; set; } = 4f;

	// Defaults
	public AssistLevel DefaultAssists { get; set; } = AssistLevel.Casual;
	public Color Tint { get; set; } = new( 0.85f, 0.55f, 0.35f );
}

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
		PartKitManifest = "models/vehicles/hatch_kit/manifest.json",
		Name = "Compact Hatch",
		Mass = 1150f,
		// belly-collider box; the kit's true extremities get their own part colliders
		BodySize = new Vector3( 3.9f, 1.75f, 1.45f ),
		Wheelbase = 2.55f,   // kit spec
		TrackWidth = 1.50f,  // kit spec
		WheelRadius = 0.30f, // kit spec
		Layout = DriveLayout.FWD,
		// tuning iter 2b: 150→162 for the 8-10 s 0-100 band (+8% mid-range; topspeed stays in band)
		PeakTorque = 162f,
		RedlineRpm = 6300f,
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
		PartKitManifest = "models/vehicles/pickup_kit/manifest.json",
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
		PeakTorque = 320f,           // torquey: >2× hatch; gear-1 drive force ~13 kN = traction-limited launch, strong hills
		IdleRpm = 650f,
		// tuning iter 2a: RedlineRpm 4700→3900 + ShiftUpRpm 4100→3500 pull top speed 51.5→~43 m/s into
		// the 140-165 km/h band via the rev ceiling (no aero drag in the model), leaving the
		// gear-1 launch torque — the signature hill-climb strength — untouched.
		RedlineRpm = 3900f,          // lowest redline in roster — low-rev truck character
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
		ReverseSpeedCap = 4.0f,
		// tuning iter 2b drift button: deepest rear cut in the roster — 1900 kg + 3.4 m wheelbase
		// never broke loose at 1.0 (J-turn no-180 through runs 1-3); the truck needs real help.
		HandbrakeGripScale = 0.45f,
		SpinRecoveryAssist = 7.0f, // spin-recovery default (tested 2026-07-15 on hatch, 6→7; other cars inherit as a starting point); tunable dial
		DefaultAssists = AssistLevel.Casual,
		Tint = new Color( 0.55f, 0.13f, 0.11f ), // matches kit body_red
	};

	public static CarDefinition Kart => new()
	{
		// Custom part-kit art. If the kit fails to assemble the factory spawns a primitive blockout
		// kart (BuildKartBody) and logs a clear error — there is no fused stand-in model.
		PartKitManifest = "models/vehicles/kart_kit/manifest.json",
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
		PeakTorque = 52f, // punchy launch, still shifts out of wheelspin quickly
		IdleRpm = 1400f,
		RedlineRpm = 9000f,
		EngineInertia = 0.05f,
		EngineBrakeTorque = 8f,
		// The kart keeps the buzzier high loop (the v1 high-pitched character "could suit the
		// kart"); base pitch >1 preserves its whine, while the narrowed band tames the redline chipmunk.
		EngineSoundEvent = "sounds/engine/engine_b_sport_purr.sound",
		EnginePitchBase = 1.2f,
		// short kart gearing: telemetry showed gear 1 topping ~50 km/h = permanent wheelspin.
		// tuning iter 2a: FinalDrive 5.0→6.3 pulls top speed 21.6→~17 m/s into the 15.3-19.4 band
		// while keeping the high-rev kart character (redline unchanged).
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
		PartKitManifest = "models/vehicles/coupe_kit/manifest.json",
		Name = "Sports Coupe",
		Mass = 1420f,
		BodySize = new Vector3( 4.4f, 1.85f, 1.25f ),
		Wheelbase = 2.7f,
		TrackWidth = 1.60f,
		Layout = DriveLayout.RWD,
		PeakTorque = 340f,
		RedlineRpm = 7200f,
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
