namespace VehicleProto;

/// <summary>
/// Engine + clutch + gearbox + diff state machine (spec §5.2.2). Plain class owned by
/// VehicleController, simulated per substep. RPM is its own integrated state coupled to
/// the wheels through an auto-clutch, so revving at standstill and shift flare exist.
/// </summary>
public class Drivetrain
{
	readonly CarDefinition _def;

	public float Rpm { get; private set; }
	public int Gear { get; private set; } = 1; // 1-based; 0 = neutral, -1 = reverse
	public bool IsShifting => _shiftTimer > 0f;

	/// <summary>Driver-selectable transmission mode. false (default) = the untuned automatic box
	/// (byte-identical existing behavior); true = sequential MANUAL — the auto-shift block in
	/// <see cref="Simulate"/> is suppressed and gear changes come from <see cref="ShiftUp"/> /
	/// <see cref="ShiftDown"/> driven off input. The rev limiter, shift timer/lockout, and clutch
	/// machinery are untouched by the mode (they model the shift event either way).</summary>
	public bool ManualMode { get; set; }

	/// <summary>Redline (rpm) for this car — read by the manual over-rev guard / UI.</summary>
	public float Redline => _def.RedlineRpm;

	float _shiftTimer;
	float _shiftLockout;
	float _freeRpm; // engine-side rpm when the clutch slips
	float _limiterHold; // seconds spent pinned near redline under power (limiter-camp escape)

	public Drivetrain( CarDefinition def )
	{
		_def = def;
		Rpm = def.IdleRpm;
		_freeRpm = def.IdleRpm;
	}

	/// <summary>Analytic torque curve: ~50% at idle, peak ~75% of the band, mild falloff at redline.</summary>
	public float EngineTorqueAt( float rpm )
	{
		float n = Math.Clamp( (rpm - _def.IdleRpm) / (_def.RedlineRpm - _def.IdleRpm), 0f, 1f );
		float shape = (0.5f + 1.5f * n - n * n) / 1.0625f;
		return _def.PeakTorque * shape;
	}

	float CurrentRatio => Gear switch
	{
		> 0 => _def.GearRatios[Gear - 1] * _def.FinalDrive,
		-1 => -_def.ReverseRatio * _def.FinalDrive,
		_ => 0f
	};

	/// <summary>
	/// One substep. avgDrivenWheelSpeed and groundWheelSpeed in rad/s. Returns torque per driven
	/// wheel (N·m). Clutch engagement is a continuous blend — a binary locked/slipping switch
	/// sits right at town-driving speeds and judders. Shift decisions use GROUND speed, not
	/// engine/wheel rpm: wheelspin inflates engine rpm, causing 1-2-1-2 shift hunting where
	/// every downshift torque-spikes the rear tires (spin-outs).
	/// </summary>
	public float Simulate( float dt, float throttle, float avgDrivenWheelSpeed, float groundWheelSpeed, int drivenWheelCount )
	{
		if ( _shiftTimer > 0f )
		{
			_shiftTimer -= dt;
			throttle = 0f; // torque cut during shift
		}

		float ratio = CurrentRatio;
		float wheelImpliedRpm = MathF.Abs( avgDrivenWheelSpeed * ratio ) * 60f / MathF.Tau;

		// continuous auto-clutch: 0 at stall rpm, fully locked a few hundred rpm above idle
		float stallRpm = _def.IdleRpm * 1.05f;
		float lockRpm = _def.IdleRpm * 1.7f;
		float engagement = Gear == 0 ? 0f : Math.Clamp( (wheelImpliedRpm - stallRpm) / (lockRpm - stallRpm), 0f, 1f );

		// engine-side rpm when slipping: revs toward the throttle target
		float freeTarget = _def.IdleRpm + throttle * (_def.RedlineRpm * 0.5f - _def.IdleRpm);
		_freeRpm = Math.Clamp( _freeRpm + (freeTarget - _freeRpm) * 5f * dt, _def.IdleRpm, _def.RedlineRpm );

		float lockedRpm = Math.Clamp( wheelImpliedRpm, _def.IdleRpm, _def.RedlineRpm );
		Rpm = MathX.Lerp( _freeRpm, lockedRpm, engagement );

		float engineTorque = EngineTorqueAt( Rpm ) * throttle;
		float engineBrake = _def.EngineBrakeTorque * (1f - throttle) * (Rpm / _def.RedlineRpm) * engagement;
		float slipTransmission = Math.Clamp( throttle * 1.2f, 0f, 1f ) * 0.85f;
		float clutchFactor = MathX.Lerp( slipTransmission, 1f, engagement );
		float torqueOut = (engineTorque * clutchFactor - engineBrake) * ratio;

		// rev limiter
		if ( Rpm >= _def.RedlineRpm && throttle > 0f )
			torqueOut = MathF.Min( torqueOut, 0f );

		// automatic shifting from ground-speed-implied rpm + post-shift lockout
		_shiftLockout -= dt;
		float groundRpm = MathF.Abs( groundWheelSpeed * ratio ) * 60f / MathF.Tau;

		// Limiter-camp escape: the upshift decision reads GROUND-speed rpm (anti-hunt — see the
		// header comment) but the engine + rev limiter run on WHEEL-implied rpm, and wheelspin
		// separates the two. With traction control off, a hard launch inflates engine rpm onto the
		// limiter while groundRpm is still below ShiftUpRpm — the box then bounces on the limiter
		// until ground speed catches up (measured on the hatch: ~3.4 s pinned 5945–6300 in 1st
		// while groundRpm crawled 560→5730). The limiter cut decelerates a spinning wheel within
		// substeps, so the bounce dips a few percent below redline many times a second — the
		// near-limiter window is therefore WIDE (0.94) and the hold DECAYS on dips instead of
		// resetting, or the oscillation zeroes the timer forever and the escape never fires.
		bool nearLimiter = Rpm >= _def.RedlineRpm * 0.94f && throttle > 0.5f;
		_limiterHold = nearLimiter ? _limiterHold + dt : MathF.Max( 0f, _limiterHold - dt * 0.5f );

		if ( !ManualMode && Gear > 0 && !IsShifting && _shiftLockout <= 0f )
		{
			bool wantUp = groundRpm > _def.ShiftUpRpm;
			bool escape = false;
			if ( !wantUp && _limiterHold > 0.25f && Gear < _def.GearRatios.Length )
			{
				// Escape guard: the next gear only needs to be VIABLE, not already above the
				// downshift point — a spinning launch hooks up instantly on the taller gear and
				// climbs fast (measured ~940 rpm/s in 2nd at full throttle), so a half-ShiftDown
				// floor plus the extended lockout below bridges the recovery without hunting.
				float nextRatio = _def.GearRatios[Gear] * _def.FinalDrive; // Gear is 1-based → [Gear] = next gear up
				float postShiftGroundRpm = MathF.Abs( groundWheelSpeed * nextRatio ) * 60f / MathF.Tau;
				wantUp = escape = postShiftGroundRpm >= _def.ShiftDownRpm * 0.5f;
			}

			if ( wantUp && Gear < _def.GearRatios.Length )
			{
				Gear++;
				_shiftTimer = 0.15f;
				// Escape shifts get a longer post-shift hold: ground rpm in the new gear may start
				// below ShiftDownRpm and needs time to climb past it, or the box would downshift
				// straight back into the wheelspin that caused the escape (the 1-2-1-2 hunt).
				_shiftLockout = escape ? 1.5f : 0.8f;
				_limiterHold = 0f;
			}
			else if ( groundRpm < _def.ShiftDownRpm && Gear > 1 )
			{
				Gear--;
				_shiftTimer = 0.12f;
				_shiftLockout = 0.8f;
			}
		}

		// open diff v1: equal split across driven wheels
		return drivenWheelCount > 0 ? torqueOut / drivenWheelCount : 0f;
	}

	public void EngageReverse() { Gear = -1; _shiftTimer = 0f; }
	public void EngageForward() { if ( Gear <= 0 ) { Gear = 1; _shiftTimer = 0f; } }

	// ── sequential MANUAL shift (feature 2026-07-15) ──
	// Gated on IsShifting only (the 0.15 s torque-cut window), NOT on _shiftLockout — the 0.8 s
	// lockout is the auto box's anti-hunt guard and would make a hand-shifted sequential box feel
	// sluggish; the player decides when to shift. Both timers are still set so the shift flare /
	// torque cut model exactly as the auto path, and so toggling back to AUTO inherits a clean state.

	/// <summary>Sequential manual up-shift: advance one forward gear. No-op in reverse/neutral, at the
	/// top gear, or mid-shift. Returns true if a gear change happened.</summary>
	public bool ShiftUp()
	{
		if ( IsShifting ) return false;
		if ( Gear <= 0 || Gear >= _def.GearRatios.Length ) return false;
		Gear++;
		_shiftTimer = 0.15f;
		_shiftLockout = 0.8f;
		return true;
	}

	/// <summary>Sequential manual down-shift: drop one forward gear — BLOCKED if it would throw engine
	/// rpm past redline at the current ground speed (money-shift / over-rev guard). No-op below 1st, in
	/// reverse/neutral, or mid-shift. <paramref name="groundWheelSpeed"/> is the ground-implied wheel
	/// speed (rad/s), the same quantity the auto path shifts on. Returns true if a gear change happened.</summary>
	public bool ShiftDown( float groundWheelSpeed )
	{
		if ( IsShifting ) return false;
		if ( Gear <= 1 ) return false; // below 1st does nothing; reverse engages automatically (ReadInput)
		if ( PredictedDownshiftRpm( groundWheelSpeed ) > _def.RedlineRpm ) return false; // over-rev guard
		Gear--;
		_shiftTimer = 0.12f;
		_shiftLockout = 0.8f;
		return true;
	}

	/// <summary>Engine rpm a one-gear downshift would produce at this ground wheel speed (rad/s), using
	/// the SAME ground-speed→rpm mapping as the auto-shift decision so the over-rev guard and the rev
	/// limiter agree. Returns the current <see cref="Rpm"/> when there is no lower forward gear.</summary>
	public float PredictedDownshiftRpm( float groundWheelSpeed )
	{
		if ( Gear <= 1 ) return Rpm;
		float lowerRatio = _def.GearRatios[Gear - 2] * _def.FinalDrive;
		return MathF.Abs( groundWheelSpeed * lowerRatio ) * 60f / MathF.Tau;
	}
}
