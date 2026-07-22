namespace FieldGuide.VehiclePhysics;

/// <summary>
/// One wheel: shapecast ground detection + spring/damper suspension (skeleton pattern from
/// Facepunch/sbox-libwheel, MIT) with the friction model replaced by the spec §5.2.1 slip
/// physics — wheel angular velocity as integrated state, slip ratio/angle into peaked curves,
/// friction ellipse, load sensitivity, low-speed blend.
///
/// Driven by <see cref="VehicleController"/> in substeps; forces are accumulated across
/// substeps and applied once per fixed update (ApplyForceAt applies for the whole step).
/// All internal math in SI.
/// </summary>
public sealed class VehicleWheel : Component
{
	// Configured by VehicleFactory from CarDefinition
	public float Radius { get; set; } = 0.31f;
	public float Inertia { get; set; } = 1.2f;
	public float SuspensionTravel { get; set; } = 0.18f;
	public float SpringRate { get; set; } = 38000f;
	public float DamperRate { get; set; } = 2800f;
	public TireCurve LongitudinalCurve { get; set; }
	public TireCurve LateralCurve { get; set; }
	public float LoadSensitivity { get; set; } = 0.06f;
	public float StaticLoad { get; set; } = 3000f; // N, set by factory: mass·g/4
	public bool IsSteering { get; set; }
	public bool IsDriven { get; set; }
	public bool HasHandbrake { get; set; }
	public float GripScale { get; set; } = 1f; // live multiplier, e.g. handbrake drift cuts rear grip
	public float ParkBrakeScale { get; set; } = 1f; // anti-jitter stiction strength; controller fades it with throttle

	/// <summary>Per-substep DRIVE-side angular-velocity cap (rad/s), set by the controller each
	/// substep to the drivetrain's redline-equivalent wheel speed for the current gear (see
	/// <see cref="Drivetrain.RedlineWheelSpeed"/>). Drive torque can never push the wheel PAST this
	/// within a substep; the ground (tire reaction) still can, and a pre-existing overspeed is never
	/// yanked down (no phantom braking). float.MaxValue = no cap (undriven wheels, neutral).</summary>
	public float DriveOmegaCap { get; set; } = float.MaxValue;

	// State
	public float AngularVelocity { get; private set; } // rad/s, +forward
	public float SteerAngle { get; set; } // degrees, set by controller
	public float SlipRatio { get; private set; }
	public float SlipAngle { get; private set; } // radians
	public float Load { get; private set; } // N
	public bool IsGrounded => _trace.Hit;
	public float GroundSpeed { get; private set; } // m/s along wheel forward
	public float SuspensionLength { get; private set; } // m, attach point to wheel center, for visuals

	public string DebugTrace => !_trace.Hit
		? "miss"
		: $"{_trace.GameObject?.Name ?? "?"}{(_trace.StartedSolid ? "!SOLID" : "")} d{_trace.Distance:F1}u n{_trace.Normal.z:F2} sf{(_trace.Surface?.Friction ?? -1f):F2}";

	const float TraceSphereRadius = 1f * Units.UnitsToMeters; // shapecast sphere, see DoTrace

	Rigidbody _rigidbody;
	SceneTraceResult _trace;
	Vector3 _accumulatedForce; // N, world space
	Vector3 _forcePosition;
	int _substeps;
	float _smoothedSlipAngle;

	protected override void OnEnabled()
	{
		_rigidbody = Components.GetInAncestorsOrSelf<Rigidbody>();
	}

	/// <summary>Trace and reset accumulators. Call once per fixed update, before substeps.</summary>
	public void BeginStep()
	{
		DoTrace();
		_accumulatedForce = Vector3.Zero;
		_substeps = 0;
	}

	/// <summary>
	/// One physics substep. driveTorque/brakeTorque in N·m. Accumulates chassis force,
	/// integrates wheel spin.
	/// </summary>
	public void Substep( float dt, float driveTorque, float brakeTorque )
	{
		_substeps++;

		var up = _rigidbody.WorldRotation.Up;

		// a grazing contact (car tipped over, wheel scraping a wall) is not suspension:
		// generating spring force there self-propels a flipped car along the ground
		bool validContact = IsGrounded && Vector3.Dot( _trace.Normal, up ) > 0.4f;

		if ( !validContact )
		{
			Load = 0f;
			SlipRatio = 0f;
			SlipAngle = 0f;
			SuspensionLength = SuspensionTravel;
			// AIRBORNE DRIVE GATE (ramp lip-cluster fix 2026-07-21, LIVE-UNVERIFIED): a driven wheel
			// with no valid contact gets ZERO drive torque - it freewheels (brakes still act). With
			// drive torque passed through, an unloaded driven wheel flared to redline-equivalent
			// within ~3 ticks of leaving a kicker lip, the engine pinned at 94-98% redline for the
			// whole flight (live capture: rpm 6008-6010 at the lip), the drivetrain's limiter-camp
			// escape upshifted MID-AIR ~0.27 s into every full-throttle flight at 20-30 m/s, and the
			// car touched down at the flared surface speed: slip +0.4..+0.5, a 10-12 kN (~1 g) tire
			// spike for ~2 ticks, the skid chirp, and a landing one gear too tall (post-landing drive
			// force -26..-37% vs pre-lip) - the felt "hitch going off the ramps" at any speed
			// (offline quantification: tools/ramp_lip_drivetrain_port.py). Zeroing drive here starves
			// that whole cascade: omega holds ~rolling speed, rpm stays steady, the escape shift
			// never arms in air, and touchdown slip is ~0. Flat-ground behavior is byte-identical BY
			// CONSTRUCTION (this branch never runs with valid ground contact; grounded-tick A/B in
			// the port asserts bit-identical trajectories). Registered prediction at commit time: the
			// owner's Sim-mode discriminator will NOT kill the hitch (the cascade is
			// assist-independent); result to be recorded next to this comment either way.
			IntegrateWheelSpin( dt, 0f, brakeTorque, 0f );
			// Airborne wind-down is bearing drag only: 2%/s. The previous 0.5f here was 50%/s
			// (documented in round 4 as "0.5%/s", a 100x misread of its own constant): after a
			// 1.2 s flight the wheels arrived at ~55% of road speed and braked the car while
			// spinning back up (flight recorder 2026-07-21: ~2 m/s of the touchdown loss plus
			// the landing skid chirp came from exactly this). Real free wheels barely slow.
			AngularVelocity *= 1f - 0.02f * dt;
			return;
		}

		// --- suspension ---
		// Force acts along the CONTACT NORMAL, not body-up: body-up creates a feedback
		// loop where a tilted car pushes itself sideways, slides, and flips.
		// Compression is clamped to physical travel: bottoming out is a rigid contact
		// for the body collider (bump stop), not unbounded spring force.
		var normal = _trace.Normal;
		float restLength = SuspensionTravel + Radius;
		float hitLength = _trace.Distance * Units.UnitsToMeters + TraceSphereRadius; // sphere stops one radius short
		float compression = Math.Clamp( restLength - hitLength, 0f, SuspensionTravel );
		SuspensionLength = Math.Clamp( hitLength - Radius, 0f, SuspensionTravel );

		var velAtWheel = _rigidbody.GetVelocityAtPoint( WorldPosition ) * Units.UnitsToMeters;
		float compressionSpeed = -Vector3.Dot( velAtWheel, normal );

		float springForce = SpringRate * compression + DamperRate * compressionSpeed;
		Load = Math.Clamp( springForce, 0f, StaticLoad * 4f );

		_accumulatedForce += normal * Load;
		_forcePosition = _trace.EndPosition;

		// --- tire frame on the contact plane ---
		var steerRot = _rigidbody.WorldRotation * Rotation.FromYaw( SteerAngle );
		var forward = (steerRot.Forward - normal * Vector3.Dot( steerRot.Forward, normal )).Normal;
		var side = Vector3.Cross( normal, forward );

		var contactVel = _rigidbody.GetVelocityAtPoint( _trace.EndPosition ) * Units.UnitsToMeters;
		float vLong = Vector3.Dot( contactVel, forward );
		float vLat = Vector3.Dot( contactVel, side );
		GroundSpeed = vLong;

		// --- slip ---
		// longitudinal uses RAW slip + a one-substep force clamp below (smoothing it added
		// ~100 ms of feedback lag that turned wheel spin into a rhythmic surge);
		// lateral keeps light relaxation, its loop is chassis-side and much slower
		float slipVelocity = AngularVelocity * Radius - vLong; // m/s at the contact patch
		SlipRatio = slipVelocity / MathF.Max( MathF.Abs( vLong ), 2.0f );

		float rawSlipAngle = MathF.Atan2( vLat, MathF.Abs( vLong ) + 0.7f );
		float relax = Math.Clamp( (MathF.Abs( vLong ) + 1f) * dt / 0.2f, 0.1f, 1f );
		_smoothedSlipAngle += (rawSlipAngle - _smoothedSlipAngle) * relax;
		SlipAngle = _smoothedSlipAngle;

		// --- forces from curves, load sensitivity, surface grip ---
		float surfaceGrip = _trace.Surface?.Friction ?? 1f;
		float loadFactor = 1f - LoadSensitivity * MathF.Max( 0f, Load / StaticLoad - 1f );
		float maxForce = Load * loadFactor * surfaceGrip * GripScale;

		float fx = LongitudinalCurve.Evaluate( SlipRatio ) * MathF.Sign( SlipRatio ) * maxForce;
		float fy = -LateralCurve.Evaluate( SlipAngle ) * MathF.Sign( SlipAngle ) * maxForce;

		// one-substep stability clamp: never push the wheel past ground-speed match within
		// a single substep (gain*dt/inertia > 2 here, the raw loop oscillates without this)
		float fxStable = MathF.Abs( slipVelocity ) * Inertia / (Radius * Radius * dt);
		fx = Math.Clamp( fx, -fxStable, fxStable );

		// --- friction ellipse (spec §5.2.1.4) ---
		float combined = MathF.Sqrt( fx * fx + fy * fy );
		if ( combined > maxForce && combined > 0.001f )
		{
			float scale = maxForce / combined;
			fx *= scale;
			fy *= scale;
		}

		// --- low-speed parking blend: kill standstill jitter ---
		// force is capped at what stops this wheel's mass share within one fixed frame
		// (spec 5.2.1.6) — uncapped, it overshoots and becomes a self-sustaining oscillator
		var planarVel = forward * vLong + side * vLat;
		float planarSpeed = planarVel.Length;
		float parkOmegaBlend = 0f;
		if ( planarSpeed < 1.5f && MathF.Abs( AngularVelocity * Radius ) < 1.5f && planarSpeed > 0.001f )
		{
			// ParkBrakeScale: throttle dissolves the stiction — steered fronts were
			// "parking" against full-lock standstill launches (2 km/h crawls, tele 07-07)
			float blend = (1f - planarSpeed / 1.5f) * ParkBrakeScale;
			float massShare = StaticLoad / 9.81f; // kg carried by this wheel
			float frameDt = dt * VehicleController.Substeps;
			float stopForce = massShare * planarSpeed / frameDt * 0.8f;
			float parkMag = MathF.Min( MathF.Min( planarSpeed * Load * 1.5f, stopForce ), maxForce );

			var park = -planarVel / planarSpeed * parkMag;
			fx = fx * (1f - blend) + Vector3.Dot( park, forward ) * blend;
			fy = fy * (1f - blend) + Vector3.Dot( park, side ) * blend;
			parkOmegaBlend = blend;
		}

		_accumulatedForce += forward * fx + side * fy;

		IntegrateWheelSpin( dt, driveTorque, brakeTorque, fx );

		// Park the wheel's SPIN, not just the chassis. The parking blend above brakes the chassis, but
		// its reaction torque (-fx*Radius, fed into IntegrateWheelSpin) spins this wheel up and, with
		// nothing anchoring omega at rest, it settles at a nonzero spin that keeps pushing the car: a
		// permanent slow creep with the tyre visibly rotating and the slip ratio pinging the skid
		// threshold (community report: "unless you've perfectly stopped, it infinitely skids and rotates
		// the wheels in place"). Pull omega toward the ground-rolling speed vLong/Radius (= 0 at a true
		// standstill) by the SAME blend, so a parked, unbraked wheel settles to zero spin and zero slip.
		// Offline sim: a 0.2 m/s creep that never stopped and skidded 50-100% of frames now settles to
		// under 1 mm/s with zero skid, while cruise (over 1.5 m/s, blend inert) is byte-identical. Fades
		// out with throttle (blend carries ParkBrakeScale) so standstill launches are unaffected.
		if ( parkOmegaBlend > 0f )
			AngularVelocity = MathX.Lerp( AngularVelocity, vLong / Radius, parkOmegaBlend );
	}

	// Cap-aware drive-torque rolloff onset (kart cap-camping fix 2026-07-18): drive torque begins
	// fading toward zero once the wheel's own spin reaches this fraction of DriveOmegaCap; below it
	// the rolloff is inert so below-cap behavior is byte-identical.
	const float DriveRolloffOnset = 0.90f;

	void IntegrateWheelSpin( float dt, float driveTorque, float brakeTorque, float tireForce )
	{
		float preOmega = AngularVelocity;
		bool driving = driveTorque != 0f;
		float driveDir = driving ? MathF.Sign( driveTorque ) : 0f;

		// Cap-aware drive-torque rolloff (kart "stuck turning" fix 2026-07-18). The per-substep
		// clamp below is a hard backstop, but with the clamp ALONE a driven wheel under sustained
		// full torque CAMPS exactly at the cap; when forward speed then collapses in a corner the
		// slip ratio blows far past the grip peak (live: 7+) and the longitudinal tail force eats the
		// friction ellipse, killing rear lateral grip so the yaw holds against countersteer. Fade
		// drive torque to zero as the wheel approaches the cap so it settles OFF the cap instead of
		// camping on it. Smoothstep, not a hard corner, to avoid a torque-fade limit cycle. Inert
		// below the onset (approach <= DriveRolloffOnset) so below-cap behavior is byte-identical.
		if ( driving && DriveOmegaCap < float.MaxValue )
		{
			float approach = driveDir * preOmega / DriveOmegaCap;
			if ( approach > DriveRolloffOnset )
			{
				float f = Math.Clamp( (1f - approach) / (1f - DriveRolloffOnset), 0f, 1f );
				driveTorque *= f * f * (3f - 2f * f);
			}
		}

		// drive + tire reaction
		float torque = driveTorque - tireForce * Radius;
		AngularVelocity += torque / Inertia * dt;

		// Per-substep drive-side overshoot clamp (kart high-PeakTorque wobble hunt 2026-07-18).
		// The rev limiter zeroes torque only on the substep AFTER wheel-implied rpm crosses redline,
		// so one substep of unlimited drive torque on a light wheel overshoots redline-equivalent
		// omega by up to 6-8x (measured live: kart at 900 N-m spikes to 289-339 rad/s vs the 44 rad/s
		// gear-1 redline equivalent; even stock 52 N-m reaches 141). The spike is what lets an
		// unloaded rear diverge violently from its loaded twin over any perturbation (the felt
		// "individual tires have different traction" left-right wobble). Clamp: DRIVE torque may
		// never push omega past the cap within a substep. Signed by drive direction so reverse works;
		// cap floors at the pre-integration omega so ground-driven overspeed (downhill coast) is
		// never yanked down, and the ground reaction path is untouched. Guarded on the ORIGINAL drive
		// intent so a rolloff that faded torque to zero still cannot let the wheel blow past the cap.
		if ( driving && DriveOmegaCap < float.MaxValue )
		{
			float cap = MathF.Max( DriveOmegaCap, driveDir * preOmega );
			AngularVelocity = driveDir > 0f
				? MathF.Min( AngularVelocity, cap )
				: MathF.Max( AngularVelocity, -cap );
		}

		// brakes can stop the wheel but never reverse it within a step
		if ( brakeTorque > 0f )
		{
			float brakeDelta = brakeTorque / Inertia * dt;
			AngularVelocity = MathF.Abs( AngularVelocity ) <= brakeDelta
				? 0f
				: AngularVelocity - MathF.Sign( AngularVelocity ) * brakeDelta;
		}
	}

	/// <summary>Apply the substep-averaged force to the chassis. Call once per fixed update.</summary>
	public void EndStep()
	{
		if ( _substeps == 0 || _accumulatedForce.IsNearZeroLength )
			return;

		var averaged = _accumulatedForce / _substeps;
		_rigidbody.ApplyForceAt( _forcePosition, averaged * Units.MetersToUnits );
	}

	void DoTrace()
	{
		var down = _rigidbody.WorldRotation.Down;
		float lengthUnits = (SuspensionTravel + Radius) * Units.MetersToUnits;

		_trace = Scene.Trace
			.Radius( 1f )
			.IgnoreGameObjectHierarchy( _rigidbody.GameObject )
			.WithoutTags( "car" )
			.FromTo( WorldPosition, WorldPosition + down * lengthUnits )
			.Run();
	}
}
