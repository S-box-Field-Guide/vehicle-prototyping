namespace VehicleProto;

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
			IntegrateWheelSpin( dt, driveTorque, brakeTorque, 0f );
			AngularVelocity *= 1f - 0.5f * dt; // free-spinning wheels wind down in the air
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
		}

		_accumulatedForce += forward * fx + side * fy;

		IntegrateWheelSpin( dt, driveTorque, brakeTorque, fx );
	}

	void IntegrateWheelSpin( float dt, float driveTorque, float brakeTorque, float tireForce )
	{
		// drive + tire reaction
		float preOmega = AngularVelocity;
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
		// never yanked down, and the ground reaction path is untouched.
		if ( driveTorque != 0f && DriveOmegaCap < float.MaxValue )
		{
			float dir = MathF.Sign( driveTorque );
			float cap = MathF.Max( DriveOmegaCap, dir * preOmega );
			AngularVelocity = dir > 0f
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
