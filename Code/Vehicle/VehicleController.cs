namespace VehicleProto;

/// <summary>
/// Drives one car: input → assists → drivetrain → wheel substeps (spec §5.2).
/// Runs 4 internal substeps per fixed update. PRECISION NOTE (audit 2026-07-12): this is
/// drivetrain/wheel-STATE substepping, not a full 200 Hz contact simulation — the ground
/// trace happens once per fixed update, the rigidbody does not advance between substeps
/// (velocity-at-contact is re-sampled against the same body state), and the accumulated
/// tire force is applied once, averaged. What the substeps genuinely refine: wheel angular
/// velocity integration (slip-ratio stability at 4× the rate), clutch/RPM coupling, and the
/// TC feedback loop. Contact/chassis transients (washboard, curb strikes, landings) resolve
/// at 50 Hz. Owner-simulated; proxies early-out (D2).
/// </summary>
public sealed class VehicleController : Component, Component.ICollisionListener
{
	public const int Substeps = 4;

	public CarDefinition Definition { get; set; }
	public List<VehicleWheel> Wheels { get; } = new();
	public Drivetrain Drivetrain { get; private set; }

	[Property] public AssistLevel Assists { get; set; } = AssistLevel.Casual;

	public float Throttle { get; private set; }
	public float Brake { get; private set; }
	public float Steer { get; private set; } // -1..1
	public bool Handbrake { get; private set; }
	public float SpeedMs => _rigidbody.IsValid() ? (_rigidbody.Velocity * Units.UnitsToMeters).Length : 0f;

	/// <summary>
	/// Signed longitudinal speed (m/s) for the HUD speedo: the magnitude equals <see cref="SpeedMs"/>
	/// (forward reads exactly as before), and the sign is the car's travel direction along its own
	/// facing — NEGATIVE while rolling backwards. The sign source is the SAME forward-axis projection
	/// <see cref="ApplySpinRecoveryAssist"/> and gear-engage already use
	/// (velocity · <see cref="Sandbox.GameObject.WorldRotation"/>.Forward), so the speedo agrees with
	/// the house reverse-detection. DISPLAY read only — no physics or telemetry consumes it.
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
	/// Input-source seam (test pilot; forward-compat for the <see cref="DriveInputs"/>
	/// pluggable-source abstraction — keyboard/gamepad/wheel/pilot as peers at ONE seam). When
	/// non-null this value-struct is consumed by <see cref="ReadInput"/> INSTEAD of sampling live
	/// keyboard/gamepad, so whatever set it drives through the exact same input → assists →
	/// drivetrain path a human uses — it never applies forces itself (an intent-injection
	/// pattern). Null = normal keyboard/gamepad. The test pilot sets it each tick
	/// while a maneuver runs and clears it when the run ends. This is the ONLY addition to
	/// VehicleController for the harness (a deliberately narrow seam); future device
	/// sources plug in here without touching this class again.
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
		Assists = Definition.DefaultAssists;
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

		// drift-catch assist (candidate #3): arm on the handbrake RELEASE edge, compute
		// this tick's throttle factor (1 = no cut).
		if ( _wasHandbrake && !Handbrake )
			_sinceHandbrakeRelease = 0f;
		_wasHandbrake = Handbrake;
		float driftCatch = DriftCatchFactor();

		for ( int step = 0; step < Substeps; step++ )
		{
			float avgDrivenSpeed = driven.Count > 0 ? driven.Average( w => w.AngularVelocity ) : 0f;
			float throttle = ApplyTractionControl( Throttle * driftCatch, driven );
			float torquePerWheel = Drivetrain.Simulate( dt, throttle, avgDrivenSpeed, groundWheelSpeed, driven.Count ) * launchBoost;

			foreach ( var wheel in Wheels )
			{
				float drive = wheel.IsDriven ? torquePerWheel : 0f;
				float brake = BrakeTorqueFor( wheel );
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
			// car=<id> token: added because free-drive session telemetry had to be segmented
			// by redline signature because the line didn't say which car produced it.
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

		// Sequential-shift edges are latched HERE (per-frame) — Input.Pressed is frame-scoped and
		// unreliable read from OnFixedUpdate, which may run zero or several times a frame. ReadInput
		// (fixed step) consumes + clears these. A frame with no fixed tick still keeps the latch until
		// the next one, so no press is lost at any framerate.
		if ( Input.Pressed( "ShiftUp" ) ) _liveShiftUp = true;
		if ( Input.Pressed( "ShiftDown" ) ) _liveShiftDown = true;
		if ( Input.Pressed( "ShiftMode" ) ) _liveShiftMode = true;
	}

	// Gamepad tier: deadzone + response curve for the analog steer axis.
	// TODO: promote to CarDefinition dials once the controller layer gets its own tuning
	// surface — CarDefinition/CarDefinitions lives in the VehicleFactory/Parts layer, so these
	// stay VehicleController consts for now.
	const float GamepadSteerDeadzone = 0.12f;
	const float GamepadSteerCurvePower = 1.6f; // >1 softens the center for fine control, still reaches full lock

	// Analog throttle/brake tier (owner feel 2026-07-17): a small trigger deadzone so resting
	// triggers can't creep the pedals. NOTE the engine ALSO applies its own 12.5% deadzone one layer
	// down (Controller.SetAxis zeroes any |axis| <= 0.125 before Input.GetAnalog ever sees it — read
	// from sbox-public engine source), so in practice values arrive as 0 or >~0.125 and this floor is a
	// belt-and-suspenders guard that also rescales so full pull still reaches 1.0. Linear only — the ask
	// was "variable", not a shaped curve.
	const float GamepadTriggerDeadzone = 0.05f;

	/// <summary>Sample the live input devices into a DriveInputs value (this is the
	/// keyboard/gamepad source; other sources produce the same struct and set InputOverride).
	///
	/// Gamepad tier: steering rides <see cref="Input.AnalogMove"/>.y
	/// straight off the left stick — the engine computes AnalogMove from the Movement action
	/// bindings (project-setup.md) and reports it as a continuous SDL axis value, so no digitizing
	/// happens here; <see cref="ApplyGamepadSteerCurve"/> only reshapes it (deadzone + curve).
	/// Keyboard emits exact -1/0/1 through the same path and passes through unchanged (see the
	/// curve's own doc), so this one seam covers both devices without a branch.
	///
	/// Throttle/brake: VARIABLE per device (owner feel 2026-07-17). Keyboard W/S ride
	/// <see cref="Input.AnalogMove"/>.x as an exact ±1 digital forward/back; the gamepad triggers are
	/// read as a true ANALOG 0..1 pull via <c>Input.GetAnalog(InputAnalog.RightTrigger|LeftTrigger)</c>
	/// (right = gas, left = brake, matching the GasTrigger/BrakeTrigger config binds). The two devices
	/// combine per channel by MAX — throttle = max(keyboard-forward, right-trigger), brake =
	/// max(keyboard-back, left-trigger) — so either device works and neither fights the other, then the
	/// net (throttle − brake) folds back into the single signed <see cref="DriveInputs.MoveForward"/>
	/// scalar that <see cref="ReadInput"/> already splits into Throttle/Brake with the gear/reverse
	/// logic. Keyboard-only players are byte-identical: on keyboard <c>UsingController</c> is false so
	/// <c>Input.GetAnalog</c> returns 0, leaving max(±1, 0) = the old W/S value.
	///
	/// Why this is the clean read (CORRECTS the 2026-07-11 note that claimed no per-action analog axis):
	/// the confusion was conflating two different enums. <c>InputAction</c> (a named Input.config entry
	/// like GasTrigger) really is digital-only — no analog flag. But <c>InputAnalog</c> is a SEPARATE
	/// public enum that, in the installed SDK, carries explicit per-axis members —
	/// LeftStickX/Y, RightStickX/Y and, crucially, <c>LeftTrigger</c>/<c>RightTrigger</c> — and
	/// <c>Input.GetAnalog(InputAnalog)</c> is public, returning the trigger pull 0..1 (0 = released).
	/// It reads the physical trigger axis directly (independent of the GasTrigger/BrakeTrigger config
	/// action), so no NativeEngine/internal-Controller access is needed. Verified against sbox-public
	/// engine source (Systems/Input/Controller/{InputAnalog.cs,Input.Controller.cs}) AND the installed
	/// Sandbox.Engine.dll metadata (2026-07-17).
	///
	/// Handbrake: keyboard Space ("Jump") is untouched; a gamepad bumper ("Handbrake" action,
	/// SwitchLeftBumper) is ADDED alongside it in Input.config — gamepad A already reaches Jump
	/// too (Jump's own GamepadCode), so the bumper is a second, driving-specific way in.</summary>
	static DriveInputs SampleDeviceInputs()
	{
		var move = Input.AnalogMove;

		// keyboard/stick forward+back split off the shared move axis (W = +x, S = -x)
		float keyThrottle = MathF.Max( 0f, move.x );
		float keyBrake = MathF.Max( 0f, -move.x );

		// gamepad triggers, true analog 0..1 (right = gas, left = brake)
		float triggerThrottle = ReadTrigger( InputAnalog.RightTrigger );
		float triggerBrake = ReadTrigger( InputAnalog.LeftTrigger );

		// MAX blend per channel so either device drives the pedal, neither overrides the other
		float throttle = MathF.Max( keyThrottle, triggerThrottle );
		float brake = MathF.Max( keyBrake, triggerBrake );
		float moveForward = Math.Clamp( throttle - brake, -1f, 1f );

		return new DriveInputs
		{
			MoveForward = moveForward,
			Steer = ApplyGamepadSteerCurve( move.y ),
			Handbrake = Input.Down( "Jump" ) || Input.Down( "Handbrake" ),
		};
	}

	/// <summary>Read a gamepad trigger as a linear 0..1 pull with a small deadzone floor (rescaled so
	/// full pull still reaches 1.0). <c>Input.GetAnalog</c> already returns 0 for a trigger on keyboard
	/// (UsingController false) and the engine pre-applies a 12.5% deadzone, so this only shapes the
	/// gamepad path.</summary>
	static float ReadTrigger( InputAnalog trigger )
	{
		float v = Math.Clamp( Input.GetAnalog( trigger ), 0f, 1f );
		if ( v < GamepadTriggerDeadzone )
			return 0f;
		return (v - GamepadTriggerDeadzone) / (1f - GamepadTriggerDeadzone);
	}

	/// <summary>Deadzone + power curve for the analog steer axis. Values under the deadzone snap
	/// to 0; the remaining range is rescaled so full stick deflection still reaches ±1 (no lost
	/// lock), then raised to <see cref="GamepadSteerCurvePower"/> for a softer center. Keyboard's
	/// exact -1/0/1 passes through unaffected: 0 is inside the deadzone (stays 0) and 1 rescales
	/// to 1 before and after the power (1^n == 1) — so this is gamepad-only in practice despite
	/// running on every sample.</summary>
	static float ApplyGamepadSteerCurve( float raw )
	{
		float mag = MathF.Abs( raw );
		if ( mag < GamepadSteerDeadzone )
			return 0f;

		float t = Math.Clamp( (mag - GamepadSteerDeadzone) / (1f - GamepadSteerDeadzone), 0f, 1f );
		return MathF.Sign( raw ) * MathF.Pow( t, GamepadSteerCurvePower );
	}

	void ReadInput()
	{
		// one seam: live devices by default, or whatever an input source (the test pilot, an input
		// device) staged in InputOverride this tick. The struct carries the SAME raw intent the
		// keyboard sampled, so all the gear/reverse/steer-ramp logic below is source-agnostic.
		var inputs = InputOverride ?? SampleDeviceInputs();

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

		// ── sequential MANUAL shift requests (feature 2026-07-15) ──
		// Two sources at ONE seam, exactly like MoveForward/Steer/Handbrake: a scripted InputOverride
		// (the test pilot) carries the shift bits in the struct; live play uses the per-frame edges
		// latched in OnUpdate. Live latches are consumed either way so a stray key pressed during a
		// pilot run can't leak into a live shift when the override clears.
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

	// ── sequential MANUAL shift state (feature 2026-07-15) ──
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
			// Drift-exit soft-lock (feel session 2026-07-13): when a slip cap is active and this
			// rear is already sliding PAST it (SlipRatio more negative than the cap), withhold the
			// handbrake torque this substep so the wheel spins back up toward the cap — the rears keep
			// rotating and retain lateral authority instead of dead-locking into 60°+ slip angles.
			// Same one-substep-lagged SlipRatio the ABS branch below reads. Default cap -1.0 leaves
			// capActive false, so full lock (today's behavior) is byte-identical for every other car.
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
	/// Arcade SPIN-RECOVERY assist (feel session 2026-07-15). After a handbrake flick spins the
	/// car ~180°, the player holds full FORWARD throttle but the car keeps rolling BACKWARDS (its old
	/// travel direction) for too long before the tires pick up and drive it the new way. BrakeAssist
	/// can't cover this: with a forward gear + W held, ReadInput sets Throttle=1, Brake=0 — so the only
	/// thing arresting the backward slide is deep-slip tire tail grip, further scaled down by the
	/// friction ellipse sharing with lateral demand. This adds chassis-level deceleration along
	/// -flat-velocity WHENEVER the input throttle commands the gear's direction while ground velocity
	/// along the car's facing opposes it (the quadrant BrakeAssist's opposing-input→Brake mapping never
	/// reaches), fading out via an opposition ramp as the car rotates to face its motion. Capped by the
	/// same never-reverse-within-a-step stopCap BrakeAssist uses. Sim keeps the raw accepted feel.
	///
	/// Interaction with drift-catch (VehicleController.DriftCatchFactor): drift-catch cuts DRIVETRAIN
	/// throttle for ≤0.5 s after handbrake release while the rear is deeply SIDEWAYS; this reads INPUT
	/// throttle and applies a CHASSIS force. They serve different states — sideways (realign) vs
	/// backwards (kill stale velocity) — and act together in a spin-recovery WITHOUT being merged: keep
	/// them separate (a sideways car may not be backwards, and a backwards car may already be aligned).
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

		// throttled ~2 Hz while active — parseable for free-drive sessions (wallglance log style).
		if ( _recoverLog > 0.5f )
		{
			_recoverLog = 0f;
			Log.Info( $"[vp] recover v {planarSpeed * 3.6f:F0}kmh along {alongCommanded:F1}m/s ramp {oppositionRamp:F2} decel {applied:F1}m/s2" );
		}
	}

	// ── drift-catch assist (feel session 2026-07-13) ──
	// The measured drift-exit anatomy: on handbrake release the driver goes to full throttle while
	// the rear slip angle is still 60°+, so the drive torque spends the rear tires' friction ellipse
	// LONGITUDINALLY exactly when every newton of lateral force is needed to realign the velocity
	// vector ("stuck sliding sideways" + rearK 0.37 spike + auto-downshift in the telemetry).
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
		// tire-curve peaks sit at slip 0.09-0.14; the old 0.25 target parked the tire in the
		// post-peak slide — slower (less grip than the peak) AND permanently over the wheelspin
		// telemetry threshold (slip > 0.2), so every launch logged multi-second "wheelspin".
		// 0.14 targets the peak: more launch grip and slip under the counter. (tuning iteration 2a)
		const float TcSlipTarget = 0.14f;
		float worstSlip = driven.Where( w => w.IsGrounded ).Select( w => w.SlipRatio ).DefaultIfEmpty( 0f ).Max();
		if ( worstSlip <= TcSlipTarget )
			return throttle;

		return throttle * Math.Clamp( TcSlipTarget / worstSlip, 0.2f, 1f );
	}

	void ApplyStabilityAssist()
	{
		// the drift button asks for yaw — don't damp it away while held
		if ( Handbrake || Assists != AssistLevel.Casual )
			return;

		// spec 5.2.3: small corrective action when the rear steps out — damp yaw so slides
		// are catchable instead of divergent (the lift-off L-R flick spin)
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

	// ── wall-glance forgiveness assist (feel session 2026-07-12) ──
	// Detection surface = the chassis Rigidbody's collision callbacks (Component.ICollisionListener;
	// the ONLY runtime chassis-contact API — verified against Sandbox.Engine.xml, this also seeds the
	// destruction D0 impact survey: OnCollisionStart/Update/Stop carry Sandbox.Collision with
	// .Contact.Point/.Contact.Normal and .Other). The chassis box rests ABOVE the wheels' contact
	// zone, so it never touches flat ground — these fire only on real obstacle contact (wall, cone,
	// bottom-out). We latch ONLY near-horizontal normals (true walls); ground/ramps/banks have
	// near-vertical normals and are ignored.
	const float WallNormalZMax = 0.5f;   // |normal.z| < this ⇒ surface within ~30° of vertical = a wall
	// engage as soon as a wall contact is confirmed (a corner-wedge kills speed in a few frames, so a
	// multi-frame gate arrives after the dead-stop it's meant to prevent). Cone exclusion instead
	// rests on geometry: the shipping city has NO cones (only the slalom test station does), and every
	// battery slalom run records 0 cone strikes — so no [vp] wallglance line can appear in the battery
	// (a battery-run assertion). A stray ≤1-frame contact still can't engage: the streak must reach this AND
	// the car must be moving INTO the surface above the speed floor.
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
	/// hard stops (the destruction program depends on honest frontal crashes). An assist: gated on
	/// <see cref="CarDefinition.WallGlanceAssist"/> and Assists != Sim (Sim = the accepted raw feel).
	/// Runs in OnFixedUpdate BEFORE the physics step, so the solver then resolves the redirected
	/// velocity without penetration — same write-then-step pattern as the brake/stability assists.
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
		// max(natural tangential speed, WallScrubFactor·total). This is NOT a per-frame ·0.75 scale —
		// that compounds into a fast decay; here, once the car is redirected along the wall the target
		// equals the current tangential speed, so it slides steadily instead of grinding to a halt.
		// Blended by incidence so a near head-on keeps the physics dead-stop.
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

		// throttled so a multi-tick slide doesn't flood the log; tagged for the battery assertion
		// (task 6: NO [vp] wallglance lines may appear in any battery maneuver's console output).
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

/// <summary>
/// Per-frame driver intent, the value-struct swap point at VehicleController's input seam
/// (<see cref="VehicleController.InputOverride"/>). Carries the raw intent the controller
/// samples from a device: the move axis (forward/back + steer) and the handbrake action. A
/// keyboard/gamepad/wheel source or the test <c>VehiclePilot</c> all produce one of these, so
/// the controller's gear/reverse/steer-ramp logic stays source-agnostic.
///
/// This was introduced for the pilot; the gamepad tier
/// plugs the gamepad in at this SAME three-field shape rather than growing new fields: the analog
/// trigger throttle/brake MAX-blend with the keyboard/stick axis into <see cref="MoveForward"/> and the bumper handbrake
/// ORs into <see cref="Handbrake"/> inside <c>SampleDeviceInputs</c> (device sampling stays
/// entirely inside that one method — see its doc for why triggers/curve landed there instead of
/// as new struct fields). A future shift/H-shifter wheel-tier field is still open, deferred to
/// a future wheel-tier pass.
/// </summary>
public struct DriveInputs
{
	/// <summary>-1..1 signed drive axis: +forward accelerates, -back brakes then engages reverse near a
	/// stop. Built in <c>SampleDeviceInputs</c> as (throttle − brake) where each channel is the MAX of
	/// the keyboard/stick component (<c>Input.AnalogMove.x</c>) and the ANALOG gamepad trigger pull
	/// (<c>Input.GetAnalog(InputAnalog.RightTrigger|LeftTrigger)</c>, 0..1) — so a partial trigger gives
	/// a partial pedal. The test pilot sets this float directly.</summary>
	public float MoveForward;

	/// <summary>-1..1, maps to <c>Input.AnalogMove.y</c> (gamepad path reshaped by the gamepad deadzone
	/// + response curve in <c>SampleDeviceInputs</c>). Note <c>Rotation.FromYaw(+)</c> is a
	/// LEFT/CCW turn (verified in-engine), so +Steer steers left.</summary>
	public float Steer;

	/// <summary>The handbrake / drift button: keyboard "Jump" (Space), or gamepad A (Jump's own
	/// GamepadCode) or the left bumper ("Handbrake" action).</summary>
	public bool Handbrake;

	/// <summary>Edge-triggered request to shift UP one gear (sequential MANUAL mode). Keyboard E /
	/// gamepad R1 while live; a scripted source (the test pilot) pulses it for one tick. The controller
	/// rising-edge-detects it, so a source that holds it across ticks still shifts exactly once.</summary>
	public bool ShiftUp;

	/// <summary>Edge-triggered request to shift DOWN one gear (sequential MANUAL mode). Keyboard Q /
	/// gamepad L1. Same one-shot rising-edge semantics as <see cref="ShiftUp"/>.</summary>
	public bool ShiftDown;

	/// <summary>Edge-triggered request to toggle the transmission mode AUTO↔MANUAL. Keyboard G /
	/// gamepad DpadNorth. Same one-shot rising-edge semantics as <see cref="ShiftUp"/>.</summary>
	public bool ShiftModeToggle;
}
