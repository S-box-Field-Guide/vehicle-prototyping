namespace FieldGuide.VehiclePhysics;

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
	Casual, // full traction control + yaw-stability damping + ABS
	Sport,  // ABS + optional REDUCED-authority TC / yaw-stability (per-car opt-in; drift feel preserved)
	Sim     // nothing
}

/// <summary>
/// Complete tuning spec for one car. Plain class, instantiated from <see cref="CarDefinitions"/>;
/// promote to a [GameResource] .car asset once an editor workflow is in play so designers can tune
/// without recompiling. All values SI: meters, kg, N, N-m, N/m.
/// </summary>
public class CarDefinition
{
	public string Name { get; set; } = "Car";
	public BodyStyle Style { get; set; } = BodyStyle.Box;
	public bool HasDriver { get; set; }

	/// <summary>Optional opaque body-manifest path for a consumer-supplied custom body builder
	/// (see <see cref="VehicleFactory.CustomBodyBuilder"/>). The kit itself never reads it — with no
	/// custom builder plugged in, the factory builds a primitive blockout body and this value is
	/// inert. It exists so a consumer's builder (e.g. a part-kit assembler) can look up which body to
	/// assemble for this car. null keeps the blockout path byte-for-byte unchanged.</summary>
	public string BodyManifest { get; set; }

	// Chassis
	public float Mass { get; set; } = 1200f;
	public Vector3 BodySize { get; set; } = new( 4.0f, 1.8f, 1.3f ); // length, width, height (m)
	public float Wheelbase { get; set; } = 2.55f;
	public float TrackWidth { get; set; } = 1.55f;
	public float RideHeight { get; set; } = 0.35f; // chassis center above wheel attach plane
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
	// +20% speed pass 2026-07-21: baseline PeakTorque 150->180 and RedlineRpm 6500->7800 so a bare
	// kit consumer starts from the new faster baseline (the demo roster below overrides both per car).
	public float PeakTorque { get; set; } = 180f;
	public float IdleRpm { get; set; } = 900f;
	public float RedlineRpm { get; set; } = 7800f;
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

	// ABS dials (per-car). 0.3/0.55 because telemetry showed the ABS duty cycle, not tire grip, was
	// the brake limiter, and per-car ABS modulation is a legitimate class trait: the locking kart
	// needs a different release than the truck. Active in Casual + Sport.
	public float AbsSlipThreshold { get; set; } = 0.25f; // release when SlipRatio < -this under braking
	public float AbsReleaseFactor { get; set; } = 0.70f; // brake torque multiplier while released

	// Drift-exit soft-lock: cap the handbrake-INDUCED rear slip ratio. When a cap is active and a rear
	// wheel is already sliding PAST it, its handbrake torque is withheld that substep (ABS-style duty
	// cycle) so the rear spins back up toward the cap and keeps some rotation — hence some lateral bite
	// — mid-slide. Default -1.0 = NO effective cap (byte-identical full-lock behavior).
	public float HandbrakeSlipCap { get; set; } = -1.0f;

	// Steering
	public float MaxSteerAngle { get; set; } = 32f; // degrees at standstill
	public float HighSpeedSteerAngle { get; set; } = 8f; // degrees at/above 30 m/s

	// Arcade feel dials (defaults = the original sim-leaning behavior)
	public float SteerRateScale { get; set; } = 1f;    // multiplies how fast steering ramps
	// Reverse speed pass 2026-07-21 (owner feel): raised 4.5 -> 20.0 m/s (~10 -> ~45 mph). This is the
	// ACTUAL reverse cap mechanism (VehicleController cuts reverse throttle above this speed), and the
	// game roster inherits it (only the pickup used to override it). Each car is still additionally
	// limited by its reverse-gear redline-equivalent wheel speed (RedlineRpm/ReverseRatio/FinalDrive/
	// WheelRadius via Drivetrain.RedlineWheelSpeed), so torquey-but-tall reverse gears self-limit below
	// this cap (kart ~8 m/s, pickup ~12 m/s) while the cars reach the high-30s/40s mph.
	public float ReverseSpeedCap { get; set; } = 20.0f; // m/s before reverse throttle cuts
	public float LaunchBoost { get; set; } = 1f;       // torque multiplier at standstill, fades out by ~54 km/h
	public float BrakeAssist { get; set; } = 0f;       // extra chassis-level decel while braking (m/s²)
	// Spin-recovery assist: after a handbrake spin the car keeps rolling BACKWARDS (old travel
	// direction) while the player holds throttle the new way — BrakeAssist can't help there (forward
	// throttle sets Brake=0), so nothing arcade-level arrests the stale velocity. This is extra
	// chassis-level decel along -velocity, applied ONLY when input throttle opposes the ground velocity
	// along the car's facing, fading out as the car rotates to face its motion
	// (VehicleController.ApplySpinRecoveryAssist). Same m/s² unit + never-reverse-within-a-step cap as
	// BrakeAssist; gated Assists != Sim. 0 disables.
	public float SpinRecoveryAssist { get; set; } = 0f; // extra chassis decel killing stale opposing velocity (m/s²)
	public float HandbrakeGripScale { get; set; } = 1f; // rear grip multiplier while handbrake held (<1 = drift button)

	// Wall-glance forgiveness assist: a sustained near-horizontal chassis contact while moving
	// re-projects velocity along the wall tangent and gently yaws the heading to match, scaled by
	// incidence (VehicleController.ApplyWallGlanceAssist). Active only when Assists != Sim. Head-on hits
	// (incidence >= WallGlanceHeadOnDeg) get NO assist — frontal crashes stay hard stops.
	public bool WallGlanceAssist { get; set; } = true;
	public float WallScrubFactor { get; set; } = 0.75f;    // fraction of speed kept along the wall tangent on a shallow graze
	public float WallGlanceShallowDeg { get; set; } = 35f; // at/below this velocity-vs-wall incidence: full assist
	public float WallGlanceHeadOnDeg { get; set; } = 60f;  // at/above this incidence: no assist (hard stop preserved)
	public float WallAlignStrength { get; set; } = 6f;     // yaw-align rate toward the wall tangent (per second)

	// ── Sport-mode stability posture (owner call 2026-07-21) ──
	// Sport historically ran with NO traction control and NO yaw-stability damping (both Casual-only),
	// so a full-throttle RWD car spun the rears to redline and the counter-steer pendulum went divergent
	// — uncatchable spin-outs. These give Sport a REDUCED-authority version of each assist, opt-in per
	// car. Both default to 0 (disabled), so any car that doesn't set them keeps the raw "ABS only" Sport
	// and — critically — Casual and Sim stay byte-identical (the controller multiplies Casual authority
	// by exactly 1 and returns early for Sim, unchanged).

	// Sport traction control. When >0, Sport runs the same proportional TC as Casual
	// (VehicleController.ApplyTractionControl) but holds driven-wheel slip near THIS ratio instead of the
	// Casual 0.14 grip-peak target. Set it LOOSER than the peak (e.g. 0.30-0.45) so the rears still break
	// into a throttle-steerable slide (drift stays alive) while the redline free-spin that torches rear
	// lateral grip is capped. 0 = no Sport TC (raw wheelspin, the old behavior).
	public float SportTcSlipTarget { get; set; } = 0f;

	// Sport yaw-stability. When >0, Sport runs the yaw-rate damper (VehicleController.ApplyStabilityAssist)
	// at THIS fraction of the Casual authority (0-1). It bleeds the stored yaw rate that snaps a
	// counter-steered slide the OTHER way (the pendulum spin-out) so slides stay CATCHABLE, without
	// killing deliberate rotation the way full Casual authority would. 0 = no Sport yaw damping.
	public float SportStabilityScale { get; set; } = 0f;

	// Driver seated pose (citizen animgraph; sit enum: 0 none, 1-3 chair poses, 4-5 ground poses).
	// Defaults = the original upright chair pose. Made per-car so a recumbent kart driver — legs
	// extended forward to the pedals — can be authored without disturbing any upright-seated car.
	public int DriverSit { get; set; } = 1;
	public float DriverSitOffsetHeight { get; set; } = 4f;

	// Defaults
	public AssistLevel DefaultAssists { get; set; } = AssistLevel.Casual;
	public Color Tint { get; set; } = new( 0.85f, 0.55f, 0.35f );
}

/// <summary>Car roster. Hatch is the default/first car; Kart and Coupe round out the roster.
/// These are blockout-body demo definitions — physics is identical whether a car renders as a
/// primitive blockout or a consumer-supplied custom body.</summary>
public static class CarDefinitions
{
	/// <summary>The default/first roster car — an ORANGE hot hatch. FWD, mid-power, street tires.</summary>
	public static CarDefinition Hatch => new()
	{
		Name = "Compact Hatch",
		Mass = 1150f,
		BodySize = new Vector3( 3.9f, 1.75f, 1.45f ),
		Wheelbase = 2.55f,
		TrackWidth = 1.50f,
		WheelRadius = 0.30f,
		Layout = DriveLayout.FWD,
		// +20% speed pass 2026-07-21: PeakTorque 162->194.4, RedlineRpm 6300->7560 (top-gear top speed +20%).
		PeakTorque = 194.4f,
		RedlineRpm = 7560f,
		// Real recorded car set: deep muscle idle crossfaded up to a revving high layer.
		EngineSoundEvent = "sounds/engine/engine_real_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_high.sound",
		LongitudinalCurve = new TireCurve( 0.10f, 1.35f, 0.45f, 1.08f ),
		BrakeTorque = 4300f,
		LateralCurve = new TireCurve( 0.14f, 1.30f, 0.55f, 1.04f ),
		HandbrakeGripScale = 0.55f, // the drift button — rear grip cut while handbrake held; also the FWD J-turn lever
		SpinRecoveryAssist = 7.0f,
		HighSpeedSteerAngle = 9.5f,
		SpringRate = 34000f,
		DamperRate = 2500f,
		Tint = new Color( 0.93f, 0.42f, 0.03f ), // orange
	};

	/// <summary>Full-size pickup. Heaviest in roster, RWD, torquey low-rev engine, longer-travel
	/// softer suspension, offroad-leaning tires, high ride. Signature strength = hill grade.</summary>
	public static CarDefinition Pickup => new()
	{
		Name = "Utility Pickup",
		Mass = 1900f,
		BodySize = new Vector3( 5.2f, 1.95f, 1.55f ),
		Wheelbase = 3.40f,
		TrackWidth = 1.70f,
		RideHeight = 0.44f,          // high ride: the class signature (hatch 0.35)
		GroundClearance = 0.24f,
		CenterOfMassDrop = 0.15f,    // higher CoM than the cars = truck-like roll; track 1.70 keeps rollover margin
		WheelRadius = 0.35f,
		WheelInertia = 2.4f,
		SuspensionTravel = 0.26f,    // longest in roster — washboard/offroad strength
		SpringRate = 42000f,
		DamperRate = 3400f,
		LongitudinalCurve = new TireCurve( 0.14f, 1.25f, 0.60f, 1.05f ),
		LateralCurve = new TireCurve( 0.15f, 1.22f, 0.60f, 1.06f ),
		LoadSensitivity = 0.07f,
		Layout = DriveLayout.RWD,
		// +20% speed pass 2026-07-21: PeakTorque 320->384, RedlineRpm 3900->4680 (top-gear top speed +20%).
		PeakTorque = 384f,           // torquey: >2× hatch; strong hills
		IdleRpm = 650f,
		RedlineRpm = 4680f,          // lowest redline in roster; low-rev truck character (was 3900, +20% pass)
		EngineInertia = 0.5f,
		EngineBrakeTorque = 90f,     // strong engine braking downhill
		// Real recorded truck set: deeper truck idle + revving high layer; base pitch dropped.
		EngineSoundEvent = "sounds/engine/engine_real_truck_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_truck_high.sound",
		EnginePitchBase = 0.85f,
		GearRatios = new[] { 3.8f, 2.3f, 1.5f, 1.1f, 0.85f },
		ReverseRatio = 3.8f,
		FinalDrive = 3.9f,
		ShiftUpRpm = 3500f,
		ShiftDownRpm = 1700f,
		BrakeTorque = 7000f,
		BrakeBias = 0.65f,           // unladen bed = light rear axle
		HandbrakeTorque = 5000f,
		MaxSteerAngle = 27f,         // slow truck steering; long wheelbase stabilizes
		HighSpeedSteerAngle = 8f,
		SteerRateScale = 0.9f,
		// Reverse cap override removed 2026-07-21: inherits the new 20 m/s default; the pickup's tall reverse
		// gear + low redline self-limit it to ~12 m/s (~26 mph) reverse, so it stays the roster's slowest
		// reverse by gearing character rather than an artificial ~9 mph clamp.
		HandbrakeGripScale = 0.45f,  // deepest rear cut in the roster — 1900 kg + 3.4 m wheelbase needs real help
		SpinRecoveryAssist = 7.0f,
		SportTcSlipTarget = 0.30f,   // Sport keeps the torquey RWD truck from lighting the rears to redline
		SportStabilityScale = 0.5f,  // half-authority yaw damp so a Sport slide stays catchable
		DefaultAssists = AssistLevel.Casual,
		Tint = new Color( 0.55f, 0.13f, 0.11f ), // dark brick red
	};

	public static CarDefinition Kart => new()
	{
		Name = "Go-Kart",
		Style = BodyStyle.Kart,
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
		// +20% speed pass 2026-07-21: PeakTorque 52->62.4, RedlineRpm 9000->10800 (top-gear top speed +20%).
		PeakTorque = 62.4f, // punchy launch, still shifts out of wheelspin quickly
		IdleRpm = 1400f,
		RedlineRpm = 10800f,
		EngineInertia = 0.05f,
		EngineBrakeTorque = 8f,
		// The kart keeps the buzzier single loop; base pitch >1 preserves its whine, while the narrowed
		// band tames the redline chipmunk.
		EngineSoundEvent = "sounds/engine/engine_b_sport_purr.sound",
		EnginePitchBase = 1.2f,
		GearRatios = new[] { 3.4f, 2.4f, 1.8f, 1.4f, 1.1f },
		ReverseRatio = 3.4f,
		FinalDrive = 6.3f,
		ShiftUpRpm = 8000f,
		ShiftDownRpm = 4000f,
		BrakeTorque = 560f,
		HandbrakeTorque = 900f,
		MaxSteerAngle = 31f,
		HighSpeedSteerAngle = 9f,
		// sticky kart slicks
		LongitudinalCurve = new TireCurve( 0.09f, 1.55f, 0.40f, 1.24f ),
		LateralCurve = new TireCurve( 0.12f, 1.66f, 0.45f, 1.32f ),
		AbsSlipThreshold = 0.20f, // earlier release for the lockup-prone kart
		HandbrakeGripScale = 0.70f, // mild rear cut — the light kart already rotates
		SpinRecoveryAssist = 7.0f,
		SportTcSlipTarget = 0.40f,  // loosest Sport TC — the light kart is meant to be playful
		SportStabilityScale = 0.45f, // gentle yaw damp; the kart rotates easily so keep it light
		HandbrakeSlipCap = -0.7f, // keep the rears rotating mid-slide for a cleaner drift exit
		// Recumbent kart driver pose: reclines with legs extended forward to the pedals.
		DriverSit = 4,
		DriverSitOffsetHeight = 0f,
		DefaultAssists = AssistLevel.Casual, // default fun car: keep it catchable
		Tint = new Color( 0.58f, 0.83f, 0.07f ), // acid green
	};

	public static CarDefinition Coupe => new()
	{
		Name = "Sports Coupe",
		Mass = 1420f,
		BodySize = new Vector3( 4.4f, 1.85f, 1.25f ),
		Wheelbase = 2.7f,
		TrackWidth = 1.60f,
		Layout = DriveLayout.RWD,
		// +20% speed pass 2026-07-21: PeakTorque 340->408, RedlineRpm 7200->8640 (top-gear top speed +20%).
		// ShiftUpRpm left at 6600 (unchanged) so the shift ladder holds in ground-speed terms; only the
		// top gear extends to the new redline.
		PeakTorque = 408f,
		RedlineRpm = 8640f,
		ShiftUpRpm = 6600f,
		// Real recorded car set (shared with the hatch): deep muscle idle crossfaded up to a
		// revving high layer; base pitch neutral so it sweeps the band cleanly.
		EngineSoundEvent = "sounds/engine/engine_real_low.sound",
		EngineSoundEventHigh = "sounds/engine/engine_real_high.sound",
		EnginePitchBase = 1.0f,
		LongitudinalCurve = new TireCurve( 0.09f, 1.50f, 0.40f, 1.20f ),
		LateralCurve = new TireCurve( 0.13f, 1.69f, 0.50f, 1.36f ),
		HandbrakeGripScale = 0.55f, // rears break loose under handbrake (J-turn initiation)
		HandbrakeSlipCap = -0.7f,
		SpringRate = 46000f,
		DamperRate = 3600f,
		BrakeTorque = 6200f,
		WheelRadius = 0.33f,
		SpinRecoveryAssist = 7.0f,
		SportTcSlipTarget = 0.35f,   // Sport TC: rears still slide on throttle but can't free-spin to redline
		SportStabilityScale = 0.5f,  // half-authority yaw damp: the counter-steer pendulum stays catchable
		MaxSteerAngle = 30f,
		HighSpeedSteerAngle = 10f, // sportiest car gets the most high-speed turn-in
		Tint = new Color( 0.80f, 0.05f, 0.07f ), // bright signal red
	};
}
