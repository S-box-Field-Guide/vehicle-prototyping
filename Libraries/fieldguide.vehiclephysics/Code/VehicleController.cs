namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Drives one car: input → assists → drivetrain → wheel substeps.
/// Runs 4 internal substeps per fixed update. PRECISION NOTE: this is drivetrain/wheel-STATE
/// substepping, not a full 200 Hz contact simulation — the ground trace happens once per fixed
/// update, the rigidbody does not advance between substeps (velocity-at-contact is re-sampled
/// against the same body state), and the accumulated tire force is applied once, averaged. What the
/// substeps genuinely refine: wheel angular velocity integration (slip-ratio stability at 4× the
/// rate), clutch/RPM coupling, and the TC feedback loop. Contact/chassis transients (washboard, curb
/// strikes, landings) resolve at 50 Hz. Owner-simulated; proxies early-out.
/// </summary>
public sealed class VehicleController : Component, Component.ICollisionListener
{
	public const int Substeps = 4;

	public CarDefinition Definition { get; set; }
	public List<VehicleWheel> Wheels { get; } = new();
	public Drivetrain Drivetrain { get; private set; }

	[Property] public AssistLevel Assists { get; set; } = AssistLevel.Casual;

	/// <summary>Optional assist level a spawn path wants this car to adopt on start, instead of the
	/// definition default. Set by a spawn path that carries a chosen assist level across a respawn or
	/// car swap. It exists because <see cref="OnStart"/> runs a frame or two AFTER <c>Components.Create</c>
	/// — i.e. after the spawning code has already run — so a plain post-spawn <c>Assists = x</c> is
	/// overwritten by the default-init in <see cref="OnStart"/>. When non-null, OnStart adopts THIS
	/// value instead; null keeps the definition default. Harmless regardless of the exact create/start
	/// ordering: if OnStart happened to run first, the spawn path's direct <c>Assists</c> set still wins.</summary>
	public AssistLevel? InitialAssists { get; set; }

	public float Throttle { get; private set; }
	public float Brake { get; private set; }
	public float Steer { get; private set; } // -1..1
	public bool Handbrake { get; private set; }
	public float SpeedMs => _rigidbody.IsValid() ? (_rigidbody.Velocity * Units.UnitsToMeters).Length : 0f;

	/// <summary>
	/// Signed longitudinal speed (m/s) for a HUD speedo: the magnitude equals <see cref="SpeedMs"/>
	/// (forward reads exactly as before), and the sign is the car's travel direction along its own
	/// facing — NEGATIVE while rolling backwards. The sign source is the SAME forward-axis projection
	/// <see cref="ApplySpinRecoveryAssist"/> and gear-engage already use
	/// (velocity · <see cref="Sandbox.GameObject.WorldRotation"/>.Forward). DISPLAY read only — no
	/// physics or telemetry consumes it.
	/// </summary>
	public float SignedSpeedMs
	{
		get
		{
			if ( !_rigidbody.IsValid() )
				return 0f;

			float forward = Vector3.Dot( _rigidbody.Velocity, WorldRotation.Forward );
			return forward < 0f ? -SpeedMs : SpeedMs;
		}
	}

	/// <summary>
	/// Input-source seam (the <see cref="DriveInputs"/> pluggable-source abstraction —
	/// keyboard/gamepad/wheel/scripted pilot as peers at ONE seam). When non-null this value-struct is
	/// consumed by <see cref="ReadInput"/> INSTEAD of sampling live keyboard/gamepad, so whatever set
	/// it drives through the exact same input → assists → drivetrain path a human uses — it never
	/// applies forces itself (an intent-injection pattern). Null = normal keyboard/gamepad. A scripted
	/// source sets it each tick while it drives and clears it when done. This is the ONLY input
	/// addition to VehicleController (a deliberately narrow seam); future device sources plug in here
	/// without touching this class again.
	/// </summary>
	public DriveInputs? InputOverride { get; set; }

	Rigidbody _rigidbody;
	Vector3 _spawnPosition;
	Rotation _spawnRotation;

	protected override void OnStart()
	{
		_rigidbody = Components.Get<Rigidbody>();
		Definition ??= CarDefinitions.Hatch;
		Drivetrain = new Drivetrain( Definition );
		// Adopt a carried session mode if a spawn path staged one (car swap); otherwise the
		// definition default. Because this runs a frame or two after the car is created, a spawn
		// path can't rely on a plain post-spawn Assists set surviving — it stages InitialAssists.
		Assists = InitialAssists ?? Definition.DefaultAssists;
		_spawnPosition = WorldPosition;
		_spawnRotation = WorldRotation;

		// suspension needs continuous simulation — a sleeping body ignores our forces
		if ( _rigidbody.PhysicsBody is not null )
			_rigidbody.PhysicsBody.AutoSleep = false;

		// brief freeze so the car initializes perfectly level and still
		_rigidbody.MotionEnabled = false;
		_settleFreeze = 0.4f;
	}

	TimeSince _telemetry;
	TimeSince _sinceSpawn;
	float _settleFreeze;

	protected override void OnFixedUpdate()
	{
		if ( IsProxy || !_rigidbody.IsValid() || Drivetrain is null )
			return;

		if ( _settleFreeze > 0f )
		{
			_settleFreeze -= Time.Delta;
			if ( _settleFreeze <= 0f )
			{
				_rigidbody.MotionEnabled = true;
				_sinceSpawn = 0;
			}
			return;
		}

		ReadInput();
		ApplySteering();

		// handbrake = drift button: rears instantly lose lateral bite, snap back on release
		foreach ( var wheel in Wheels )
		{
			if ( !wheel.IsSteering )
				wheel.GripScale = Handbrake ? Definition.HandbrakeGripScale : 1f;

			// throttle dissolves low-speed parking stiction on ALL wheels so full-lock
			// launches actually launch (undriven steered fronts were the drag)
			wheel.ParkBrakeScale = 1f - Throttle;
		}

		foreach ( var wheel in Wheels )
			wheel.BeginStep();

		var driven = Wheels.Where( w => w.IsDriven ).ToList();
		float dt = Time.Delta / Substeps;

		// ground-truth wheel speed for shifting: actual forward speed over the tire radius
		float forwardSpeed = Vector3.Dot( _rigidbody.Velocity * Units.UnitsToMeters, WorldRotation.Forward );
		float groundWheelSpeed = forwardSpeed / Definition.WheelRadius;

		// arcade launch boost: extra shove off the line, fully faded by 15 m/s
		float launchBoost = MathX.Lerp( Definition.LaunchBoost, 1f, Math.Clamp( SpeedMs / 15f, 0f, 1f ) );

		// drift-catch assist: arm on the handbrake RELEASE edge, compute this tick's throttle factor.
		if ( _wasHandbrake && !Handbrake )
			_sinceHandbrakeRelease = 0f;
		_wasHandbrake = Handbrake;
		float driftCatch = DriftCatchFactor();

		for ( int step = 0; step < Substeps; step++ )
		{
			float avgDrivenSpeed = driven.Count > 0 ? driven.Average( w => w.AngularVelocity ) : 0f;
			float throttle = ApplyTractionControl( Throttle * driftCatch, driven );
			float torquePerWheel = Drivetrain.Simulate( dt, throttle, avgDrivenSpeed, groundWheelSpeed, driven.Count ) * launchBoost;

			// Drive-side omega cap, read fresh AFTER Simulate (a shift changes the ratio mid-loop):
			// drive torque may never push a driven wheel past redline-equivalent within a substep
			// (the limiter-one-substep-late overshoot defect; see VehicleWheel.IntegrateWheelSpin).
			float omegaCap = Drivetrain.RedlineWheelSpeed;

			foreach ( var wheel in Wheels )
			{
				float drive = wheel.IsDriven ? torquePerWheel : 0f;
				float brake = BrakeTorqueFor( wheel );
				if ( wheel.IsDriven )
					wheel.DriveOmegaCap = omegaCap;
				wheel.Substep( dt, drive, brake );
			}
		}

		foreach ( var wheel in Wheels )
			wheel.EndStep();

		ApplyBrakeAssist();
		ApplySpinRecoveryAssist();
		ApplyStabilityAssist();
		ApplyWallGlanceAssist();

		// dense driving telemetry: 2 Hz while moving or on input — parseable for analysis
		if ( _telemetry > 0.5f && (SpeedMs > 0.5f || Throttle > 0f || Brake > 0f) )
		{
			_telemetry = 0;
			float yawRate = _rigidbody.AngularVelocity.z.RadianToDegree();
			var rears = Wheels.Where( w => !w.IsSteering ).ToList();
			var fronts = Wheels.Where( w => w.IsSteering ).ToList();
			Log.Info( $"[vp] tele car={Definition?.Name ?? "?"} v {SpeedMs * 3.6f:F0}kmh rpm {Drivetrain.Rpm:F0} gear {Drivetrain.Gear} | thr {Throttle:F2} brk {Brake:F2} hb {(Handbrake ? 1 : 0)} steer {Steer:F2} | yaw {yawRate:F0}deg/s | rearK {rears.Average( w => w.SlipRatio ):F2} frontA {fronts.Average( w => w.SlipAngle.RadianToDegree() ):F1} rearA {rears.Average( w => w.SlipAngle.RadianToDegree() ):F1}" );

			// per-wheel trace hits whenever contact is abnormal — names what we're driving
			// on (or falling through); this diagnostic has caught three bugs, keep it
			int grounded = Wheels.Count( w => w.IsGrounded );
			if ( grounded < 4 || _sinceSpawn < 6f )
			{
				var detail = string.Join( " | ", Wheels.Select( w =>
					$"{w.GameObject.Name[^2..]} {w.DebugTrace} Fz {w.Load:F0}" ) );
				Log.Info( $"[vp] wheels z {WorldPosition.z * Units.UnitsToMeters:F1}m | {detail}" );
			}
		}
	}

	protected override void OnUpdate()
	{
		if ( IsProxy )
			return;

		if ( Input.Pressed( "Reload" ) )
			Respawn();

		// Drive-mode (assist level) cycle — Casual -> Sport -> Sim -> Casual. A plain property set,
		// so (like Reload) it reads Input.Pressed straight in OnUpdate; no fixed-step edge-latch needed.
		// A plain Assists set survives respawns/car swaps via InitialAssists and never gets clobbered
		// after OnStart.
		if ( Input.Pressed( "DriveMode" ) )
			CycleDriveMode();

		// Sequential-shift edges are latched HERE (per-frame) — Input.Pressed is frame-scoped and
		// unreliable read from OnFixedUpdate, which may run zero or several times a frame. ReadInput
		// (fixed step) consumes + clears these. A frame with no fixed tick still keeps the latch until
		// the next one, so no press is lost at any framerate.
		if ( Input.Pressed( "ShiftUp" ) ) _liveShiftUp = true;
		if ( Input.Pressed( "ShiftDown" ) ) _liveShiftDown = true;
		if ( Input.Pressed( "ShiftMode" ) ) _liveShiftMode = true;
	}

	void ReadInput()
	{
		// one seam: live devices by default, or whatever an input source (a scripted pilot, an input
		// device) staged in InputOverride this tick. The struct carries the SAME raw intent the
		// keyboard sampled, so all the gear/reverse/steer-ramp logic below is source-agnostic.
		var inputs = InputOverride ?? DriveInputs.SampleDeviceInputs();

		// forward stick/W = accelerate, back = brake — unless (near-)stopped, then back = reverse
		float forwardInput = inputs.MoveForward;
		float forwardSpeed = Vector3.Dot( _rigidbody.Velocity * Units.UnitsToMeters, WorldRotation.Forward );

		// direction changes engage while still rolling ~1 m/s the wrong way — waiting
		// for a dead stop is what made K-turns feel like a driving test
		if ( Drivetrain.Gear >= 0 )
		{
			Throttle = MathF.Max( 0f, forwardInput );
			Brake = MathF.Max( 0f, -forwardInput );

			if ( forwardInput < -0.15f && forwardSpeed < 1.0f )
				Drivetrain.EngageReverse();
		}
		else // reverse gear: back = reverse throttle, forward = brake
		{
			Throttle = MathF.Max( 0f, -forwardInput );
			Brake = MathF.Max( 0f, forwardInput );

			if ( forwardInput > 0.15f && forwardSpeed > -1.0f )
				Drivetrain.EngageForward();
		}

		Handbrake = inputs.Handbrake;

		// reverse is for maneuvering, not backward land-speed records
		if ( Drivetrain.Gear == -1 && SpeedMs > Definition.ReverseSpeedCap )
			Throttle = 0f;

		// steering: keyboard ramps, analog is direct; both rate and lock reduce with
		// speed — fast full-lock flicks at 70+ km/h were driver-induced weave
		float speedT = Math.Clamp( SpeedMs / 22f, 0f, 1f );
		float steerTarget = inputs.Steer;
		float rate = (MathF.Abs( steerTarget ) > 0.01f
			? MathX.Lerp( 4.5f, 1.8f, speedT )
			: MathX.Lerp( 6f, 3f, speedT )) * Definition.SteerRateScale;
		Steer = MathX.Lerp( Steer, steerTarget, Math.Clamp( rate * Time.Delta, 0f, 1f ) );

		// ── sequential MANUAL shift requests ──
		// Two sources at ONE seam, exactly like MoveForward/Steer/Handbrake: a scripted InputOverride
		// carries the shift bits in the struct; live play uses the per-frame edges latched in OnUpdate.
		// Live latches are consumed either way so a stray key pressed during a scripted run can't leak
		// into a live shift when the override clears.
		bool reqUp, reqDown, reqMode;
		if ( InputOverride is DriveInputs ov )
		{
			reqUp = ov.ShiftUp;
			reqDown = ov.ShiftDown;
			reqMode = ov.ShiftModeToggle;
		}
		else
		{
			reqUp = _liveShiftUp;
			reqDown = _liveShiftDown;
			reqMode = _liveShiftMode;
		}
		_liveShiftUp = _liveShiftDown = _liveShiftMode = false;

		ApplyShiftRequests( reqUp, reqDown, reqMode, forwardSpeed / Definition.WheelRadius );
	}

	// ── sequential MANUAL shift state ──
	bool _liveShiftUp, _liveShiftDown, _liveShiftMode;   // per-frame edges latched in OnUpdate
	bool _prevReqUp, _prevReqDown, _prevReqMode;          // rising-edge memory across fixed ticks

	/// <summary>Rising-edge-detect this tick's shift requests and drive the drivetrain. The live path's
	/// requests are already one-tick pulses (latched Input.Pressed edges), and this ALSO edge-gates the
	/// override/pilot path so a source that holds a bit across ticks shifts exactly once (the
	/// edge-through-InputOverride trap). <paramref name="groundWheelSpeed"/> (rad/s) feeds the
	/// down-shift over-rev guard.</summary>
	void ApplyShiftRequests( bool up, bool down, bool mode, float groundWheelSpeed )
	{
		if ( mode && !_prevReqMode )
			ToggleShiftMode();
		if ( up && !_prevReqUp )
			TryShiftUp();
		if ( down && !_prevReqDown )
			TryShiftDown( groundWheelSpeed );

		_prevReqUp = up;
		_prevReqDown = down;
		_prevReqMode = mode;
	}

	void ToggleShiftMode()
	{
		Drivetrain.ManualMode = !Drivetrain.ManualMode;
		Log.Info( $"[vp] shiftmode {(Drivetrain.ManualMode ? "MANUAL" : "AUTO")} gear {Drivetrain.Gear}" );
	}

	/// <summary>Cycle the drive mode (assist level) Casual -> Sport -> Sim -> Casual. A HUD assist chip
	/// can read <see cref="Assists"/> and flash on change, so the swap is visible on press.</summary>
	void CycleDriveMode()
	{
		Assists = Assists switch
		{
			AssistLevel.Casual => AssistLevel.Sport,
			AssistLevel.Sport => AssistLevel.Sim,
			_ => AssistLevel.Casual,
		};
		Log.Info( $"[vp] drivemode {Assists.ToString().ToUpper()}" );
	}

	void TryShiftUp()
	{
		// In AUTO a manual shift request is a no-op (the box shifts itself; mode changes only via G).
		if ( !Drivetrain.ManualMode )
			return;
		if ( Drivetrain.ShiftUp() )
			Log.Info( $"[vp] shift UP -> gear {Drivetrain.Gear}" );
	}

	void TryShiftDown( float groundWheelSpeed )
	{
		if ( !Drivetrain.ManualMode )
			return;
		if ( Drivetrain.ShiftDown( groundWheelSpeed ) )
			Log.Info( $"[vp] shift DOWN -> gear {Drivetrain.Gear}" );
		else
			Log.Info( $"[vp] shift DOWN denied gear {Drivetrain.Gear} " +
				$"(predicted {Drivetrain.PredictedDownshiftRpm( groundWheelSpeed ):F0} / redline {Drivetrain.Redline:F0})" );
	}

	void ApplySteering()
	{
		float speedFactor = Math.Clamp( SpeedMs / 22f, 0f, 1f );
		float maxAngle = MathX.Lerp( Definition.MaxSteerAngle, Definition.HighSpeedSteerAngle, speedFactor );
		float angle = Steer * maxAngle;

		foreach ( var wheel in Wheels.Where( w => w.IsSteering ) )
		{
			wheel.SteerAngle = angle;
			wheel.LocalRotation = Rotation.FromYaw( angle );
		}
	}

	float BrakeTorqueFor( VehicleWheel wheel )
	{
		bool isFront = wheel.IsSteering;
		float bias = isFront ? Definition.BrakeBias : 1f - Definition.BrakeBias;
		float torque = Brake * Definition.BrakeTorque * bias * 0.5f; // per wheel (2 per axle)

		if ( Handbrake && wheel.HasHandbrake )
		{
			// Drift-exit soft-lock: when a slip cap is active and this rear is already sliding PAST it
			// (SlipRatio more negative than the cap), withhold the handbrake torque this substep so the
			// wheel spins back up toward the cap — the rears keep rotating and retain lateral authority
			// instead of dead-locking into 60°+ slip angles. Same one-substep-lagged SlipRatio the ABS
			// branch below reads. Default cap -1.0 leaves capActive false, so full lock is byte-identical.
			bool capActive = Definition.HandbrakeSlipCap > -1f;
			if ( !capActive || wheel.SlipRatio > Definition.HandbrakeSlipCap )
				torque += Definition.HandbrakeTorque;
		}

		// ABS: release when the wheel locks under braking (Casual + Sport). Thresholds are
		// per-car dials (see CarDefinition.AbsSlipThreshold).
		if ( Assists != AssistLevel.Sim && torque > 0f && wheel.IsGrounded
			&& wheel.SlipRatio < -Definition.AbsSlipThreshold )
			torque *= Definition.AbsReleaseFactor;

		return torque;
	}

	/// <summary>
	/// Arcade brake assist: extra chassis-level deceleration while braking. The tire
	/// model alone stops at sim rates, which reads as "slow" against arcade expectations.
	/// Capped so it can never reverse the car within a step.
	/// </summary>
	void ApplyBrakeAssist()
	{
		if ( Definition.BrakeAssist <= 0f || Brake < 0.1f || SpeedMs < 0.5f )
			return;

		if ( Wheels.Count( w => w.IsGrounded ) < 2 )
			return;

		var flat = _rigidbody.Velocity.WithZ( 0f );
		if ( flat.IsNearZeroLength )
			return;

		float decel = Definition.BrakeAssist * Brake; // m/s²
		float stopCap = flat.Length * Units.UnitsToMeters / Time.Delta;
		float applied = MathF.Min( decel, stopCap );

		_rigidbody.ApplyForce( -flat.Normal * applied * Definition.Mass * Units.MetersToUnits );
	}

	TimeSince _recoverLog = 999f;

	/// <summary>
	/// Arcade SPIN-RECOVERY assist. After a handbrake flick spins the car ~180°, the player holds full
	/// FORWARD throttle but the car keeps rolling BACKWARDS (its old travel direction) for too long
	/// before the tires pick up and drive it the new way. BrakeAssist can't cover this: with a forward
	/// gear + W held, ReadInput sets Throttle=1, Brake=0 — so the only thing arresting the backward
	/// slide is deep-slip tire tail grip, further scaled down by the friction ellipse sharing with
	/// lateral demand. This adds chassis-level deceleration along -flat-velocity WHENEVER the input
	/// throttle commands the gear's direction while ground velocity along the car's facing opposes it
	/// (the quadrant BrakeAssist's opposing-input→Brake mapping never reaches), fading out via an
	/// opposition ramp as the car rotates to face its motion. Capped by the same never-reverse-within-
	/// a-step stopCap BrakeAssist uses. Sim keeps the raw accepted feel.
	///
	/// Interaction with drift-catch (DriftCatchFactor): drift-catch cuts DRIVETRAIN throttle for ≤0.5 s
	/// after handbrake release while the rear is deeply SIDEWAYS; this reads INPUT throttle and applies
	/// a CHASSIS force. They serve different states — sideways (realign) vs backwards (kill stale
	/// velocity) — and act together in a spin-recovery WITHOUT being merged.
	/// </summary>
	void ApplySpinRecoveryAssist()
	{
		if ( Definition.SpinRecoveryAssist <= 0f || Assists == AssistLevel.Sim )
			return;

		// Throttle is the gear-direction INPUT throttle magnitude set in ReadInput (before the
		// drift-catch / TC drivetrain governors scale it) — exactly the raw request this assist wants.
		if ( Throttle <= 0.1f )
			return;

		if ( Wheels.Count( w => w.IsGrounded ) < 2 )
			return;

		var flat = _rigidbody.Velocity.WithZ( 0f );
		float planarSpeed = flat.Length * Units.UnitsToMeters;
		if ( planarSpeed < 0.5f )
			return;

		// velocity along the car's facing. commandedDir folds forward/reverse gear into one sign: in a
		// forward gear the throttle commands +forward, so a NEGATIVE forwardSpeed (still sliding
		// backwards under forward throttle) is the uncovered case; in reverse gear it commands
		// -forward, so a POSITIVE forwardSpeed (reverse throttle while still rolling forward) mirrors it.
		float forwardSpeed = Vector3.Dot( _rigidbody.Velocity * Units.UnitsToMeters, WorldRotation.Forward );
		float commandedDir = Drivetrain.Gear >= 0 ? 1f : -1f;
		float alongCommanded = forwardSpeed * commandedDir; // <0 ⇒ velocity opposes the throttle direction
		if ( alongCommanded > -0.5f )
			return; // already moving the commanded way (or ~stopped) — nothing to recover

		// opposition ramp: fraction of speed pointing the wrong way — 1 when velocity fully opposes
		// facing, fading to 0 as the car rotates to face its motion (so the assist bows out on its own).
		float oppositionRamp = Math.Clamp( -alongCommanded / planarSpeed, 0f, 1f );

		float decel = Definition.SpinRecoveryAssist * oppositionRamp; // m/s²
		float stopCap = planarSpeed / Time.Delta; // never reverse the flat velocity within a step
		float applied = MathF.Min( decel, stopCap );

		_rigidbody.ApplyForce( -flat.Normal * applied * Definition.Mass * Units.MetersToUnits );

		// throttled ~2 Hz while active — parseable for free-drive sessions.
		if ( _recoverLog > 0.5f )
		{
			_recoverLog = 0f;
			Log.Info( $"[vp] recover v {planarSpeed * 3.6f:F0}kmh along {alongCommanded:F1}m/s ramp {oppositionRamp:F2} decel {applied:F1}m/s2" );
		}
	}

	// ── drift-catch assist ──
	// The measured drift-exit anatomy: on handbrake release the driver goes to full throttle while
	// the rear slip angle is still 60°+, so the drive torque spends the rear tires' friction ellipse
	// LONGITUDINALLY exactly when every newton of lateral force is needed to realign the velocity
	// vector ("stuck sliding sideways" + rearK spike + auto-downshift in the telemetry).
	// For a short window after the handbrake releases, while the rear is still deeply sideways, cut
	// throttle-induced rear slip (ramping to a full cut by DriftCatchFullCutDeg) so the ellipse serves
	// realignment first — mirrors real drift-catch technique (wait for the catch before power).
	// Casual + Sport only; Sim stays the raw accepted feel. The 20° floor keeps deliberate
	// power-oversteer (jturn rotation, small-angle slides) untouched.
	const float DriftCatchWindowS = 0.5f;    // seconds after hb release the assist can act
	const float DriftCatchStartDeg = 20f;    // rear slip angle where the cut starts
	const float DriftCatchFullCutDeg = 35f;  // rear slip angle at/past which throttle is fully cut

	bool _wasHandbrake;
	TimeSince _sinceHandbrakeRelease = 999f;

	float DriftCatchFactor()
	{
		if ( Assists == AssistLevel.Sim || Handbrake )
			return 1f;
		if ( _sinceHandbrakeRelease > DriftCatchWindowS )
			return 1f;

		var rears = Wheels.Where( w => !w.IsSteering && w.IsGrounded ).ToList();
		if ( rears.Count == 0 )
			return 1f;

		float rearADeg = MathF.Abs( rears.Average( w => w.SlipAngle ) ).RadianToDegree();
		if ( rearADeg <= DriftCatchStartDeg )
			return 1f;

		return Math.Clamp( 1f - (rearADeg - DriftCatchStartDeg) / (DriftCatchFullCutDeg - DriftCatchStartDeg), 0f, 1f );
	}

	float ApplyTractionControl( float throttle, List<VehicleWheel> driven )
	{
		// drifting is throttle-steered — TC clamping wheelspin would kill the slide
		if ( Handbrake || Assists != AssistLevel.Casual || throttle <= 0f )
			return throttle;

		// proportional: hold driven-wheel slip near the grip PEAK, not past it. The longitudinal
		// tire-curve peaks sit at slip 0.09-0.14; a 0.25 target parks the tire in the post-peak slide
		// — slower (less grip than the peak) AND permanently over the wheelspin threshold. 0.14
		// targets the peak: more launch grip and slip under the counter.
		const float TcSlipTarget = 0.14f;
		float worstSlip = driven.Where( w => w.IsGrounded ).Select( w => w.SlipRatio ).DefaultIfEmpty( 0f ).Max();
		if ( worstSlip <= TcSlipTarget )
			return throttle;

		// TC floor relaxation (kart cap-camping fix 2026-07-18): the flat 0.2 throttle floor still fed
		// enough torque to sustain a spinning rear on a light car (260 kg kart), so TC could not
		// arrest the wheelspin that pins a collapsing-corner rear far past the grip peak (the "stuck
		// turning" bug, offline: this is the decisive lever, cutting sustained rear slip 3.2 -> 0.4).
		// Once slip is deep past the tail (the longitudinal curve reaches its tail by slip 0.40;
		// TcFloorRelaxStart 1.0 is well beyond it), fade the floor toward 0 so the proportional
		// response can cut throttle to near-zero. Below TcFloorRelaxStart the floor stays 0.2 so all
		// below-threshold behavior is byte-identical.
		const float TcFloorRelaxStart = 1.0f;
		const float TcFloorRelaxEnd = 2.5f;
		float floor = 0.2f;
		if ( worstSlip > TcFloorRelaxStart )
			floor *= Math.Clamp( (TcFloorRelaxEnd - worstSlip) / (TcFloorRelaxEnd - TcFloorRelaxStart), 0f, 1f );

		return throttle * Math.Clamp( TcSlipTarget / worstSlip, floor, 1f );
	}

	void ApplyStabilityAssist()
	{
		// the drift button asks for yaw — don't damp it away while held
		if ( Handbrake || Assists != AssistLevel.Casual )
			return;

		// small corrective action when the rear steps out — damp yaw so slides are catchable
		// instead of divergent (the lift-off L-R flick spin)
		var rears = Wheels.Where( w => !w.IsSteering && w.IsGrounded ).ToList();
		if ( rears.Count == 0 )
			return;

		float rearAlpha = MathF.Abs( rears.Average( w => w.SlipAngle ) );
		if ( rearAlpha < 0.07f ) // ~4 degrees
			return;

		// scales up with speed: the flat 3f cap let a 115 km/h lift-off flick go full 360
		float speedScale = 3f + 3f * Math.Clamp( SpeedMs / 30f, 0f, 1f );
		float strength = Math.Clamp( (rearAlpha - 0.07f) * 8f, 0f, 1f ) * speedScale;
		var angular = _rigidbody.AngularVelocity;
		angular.z *= MathF.Max( 0f, 1f - strength * Time.Delta );
		_rigidbody.AngularVelocity = angular;
	}

	// ── wall-glance forgiveness assist ──
	// Detection surface = the chassis Rigidbody's collision callbacks (Component.ICollisionListener;
	// the ONLY runtime chassis-contact API: OnCollisionStart/Update/Stop carry Sandbox.Collision with
	// .Contact.Point/.Contact.Normal and .Other). The chassis box rests ABOVE the wheels' contact
	// zone, so it never touches flat ground — these fire only on real obstacle contact (wall, cone,
	// bottom-out). We latch ONLY near-horizontal normals (true walls); ground/ramps/banks have
	// near-vertical normals and are ignored.
	const float WallNormalZMax = 0.5f;   // |normal.z| < this ⇒ surface within ~30° of vertical = a wall
	// engage as soon as a wall contact is confirmed (a corner-wedge kills speed in a few frames, so a
	// multi-frame gate arrives after the dead-stop it's meant to prevent). A stray ≤1-frame contact
	// still can't engage: the streak must reach this AND the car must be moving INTO the surface above
	// the speed floor.
	const int WallEngageTicks = 1;
	const float WallGlanceMinSpeed = 3f; // m/s planar floor below which forgiveness is pointless

	Vector3 _wallNormalH;          // horizontal unit wall normal from the most recent contact
	TimeSince _sinceWallContact = 999f;
	int _wallStreak;
	TimeSince _wallGlanceLog = 999f;

	public void OnCollisionStart( Collision o ) => NoteWallContact( o );
	public void OnCollisionUpdate( Collision o ) => NoteWallContact( o );
	public void OnCollisionStop( CollisionStop o ) { }

	void NoteWallContact( Collision c )
	{
		if ( IsProxy )
			return;

		var n = c.Contact.Normal;
		// a wall = near-horizontal contact normal (surface within ~30° of vertical). Ground/ramps/
		// banks report near-vertical normals and never latch, so this can't fire driving over terrain.
		if ( MathF.Abs( n.z ) >= WallNormalZMax )
			return;

		var nH = n.WithZ( 0f );
		if ( nH.IsNearZeroLength )
			return;

		_wallNormalH = nH.Normal;
		_sinceWallContact = 0f;
	}

	/// <summary>
	/// Forgiveness for angled (esp. mid-drift) wall contact: instead of a dead stop, re-project
	/// velocity onto the wall tangent (keeping <see cref="CarDefinition.WallScrubFactor"/> of speed)
	/// and gently yaw the heading to run parallel — both scaled by incidence so HEAD-ON hits stay
	/// hard stops. An assist: gated on <see cref="CarDefinition.WallGlanceAssist"/> and Assists != Sim
	/// (Sim = the accepted raw feel). Runs in OnFixedUpdate BEFORE the physics step, so the solver then
	/// resolves the redirected velocity without penetration — same write-then-step pattern as the
	/// brake/stability assists.
	/// </summary>
	void ApplyWallGlanceAssist()
	{
		bool contactNow = _sinceWallContact <= Time.Delta * 1.5f;
		if ( !contactNow )
		{
			_wallStreak = 0;
			return;
		}
		_wallStreak++;

		if ( Definition is null || !Definition.WallGlanceAssist || Assists == AssistLevel.Sim )
			return;
		if ( _wallStreak < WallEngageTicks )
			return;

		var vel = _rigidbody.Velocity;
		var planar = vel.WithZ( 0f );
		float planarSpeedMs = planar.Length * Units.UnitsToMeters;
		if ( planarSpeedMs < WallGlanceMinSpeed )
			return;

		var nH = _wallNormalH;
		float into = Vector3.Dot( planar.Normal, nH ); // <0 ⇒ moving INTO the wall
		if ( into >= -0.01f )
			return; // already sliding along / peeling away — nothing to catch

		// incidence between velocity and the wall PLANE: 0° = grazing, 90° = straight-on
		float incidenceDeg = MathF.Asin( Math.Clamp( MathF.Abs( into ), 0f, 1f ) ).RadianToDegree();

		// full assist below shallow, none at/above head-on (frontal crashes keep the hard stop)
		float scale = incidenceDeg >= Definition.WallGlanceHeadOnDeg ? 0f
			: incidenceDeg <= Definition.WallGlanceShallowDeg ? 1f
			: (Definition.WallGlanceHeadOnDeg - incidenceDeg)
				/ MathF.Max( 1e-3f, Definition.WallGlanceHeadOnDeg - Definition.WallGlanceShallowDeg );
		if ( scale <= 0f )
			return;

		// tangent = the velocity component that runs ALONG the wall (the slide direction)
		var vTangent = planar - Vector3.Dot( planar, nH ) * nH;
		if ( vTangent.IsNearZeroLength )
			return;
		var tangent = vTangent.Normal;

		// (a) re-project velocity onto the tangent: kill the into-wall component (which would wedge
		// the rigid chassis corner and dead-stop it) and carry the slide forward at
		// max(natural tangential speed, WallScrubFactor·total). Once the car is redirected along the
		// wall the target equals the current tangential speed, so it slides steadily instead of
		// grinding to a halt. Blended by incidence so a near head-on keeps the physics dead-stop.
		float totalSpeed = planar.Length;
		float targetSpeed = MathF.Min( totalSpeed,
			MathF.Max( vTangent.Length, totalSpeed * Definition.WallScrubFactor ) );
		var newPlanar = Vector3.Lerp( planar, tangent * targetSpeed, scale );
		_rigidbody.Velocity = newPlanar.WithZ( vel.z );

		// (b) gentle yaw torque aligning heading to the wall tangent (whichever way the car faces)
		var fwd = WorldRotation.Forward.WithZ( 0f ).Normal;
		var alignTo = Vector3.Dot( fwd, tangent ) < 0f ? -tangent : tangent;
		float cross = fwd.x * alignTo.y - fwd.y * alignTo.x; // +z ⇒ target is to the LEFT (CCW)
		float yawErrRad = MathF.Atan2( cross, Vector3.Dot( fwd, alignTo ) );
		var ang = _rigidbody.AngularVelocity;
		ang.z = MathX.Lerp( ang.z, yawErrRad * Definition.WallAlignStrength,
			Math.Clamp( Definition.WallAlignStrength * scale * Time.Delta, 0f, 1f ) );
		_rigidbody.AngularVelocity = ang;

		// throttled so a multi-tick slide doesn't flood the log
		if ( _wallGlanceLog > 0.2f )
		{
			_wallGlanceLog = 0f;
			Log.Info( $"[vp] wallglance inc {incidenceDeg:F0}deg scale {scale:F2} v {planarSpeedMs * 3.6f:F0}->{newPlanar.Length * Units.UnitsToMeters * 3.6f:F0}kmh" );
		}
	}

	public void Respawn()
	{
		WorldPosition = _spawnPosition + Vector3.Up * 20f;
		WorldRotation = _spawnRotation;
		_rigidbody.Velocity = Vector3.Zero;
		_rigidbody.AngularVelocity = Vector3.Zero;
	}
}
