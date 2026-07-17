namespace VehicleProto;

/// <summary>
/// Owns all per-run telemetry accumulation state + math extracted from the monolithic
/// <see cref="VehiclePilot"/> (the pilot decomposition). Each fixed
/// tick the pilot calls <see cref="Accumulate"/>, which writes the frozen telemetry contract fields
/// (docs/testing-harness.md §6.2) into <see cref="VehicleBridge"/> exactly as the old
/// <c>AccumulateTelemetry</c> did — kinematics, per-axis g decomposition, cumulative yaw,
/// airtime/contact-loss (gated on first real ground contact), landing attitude + settle. Maneuver
/// objects READ the cumulative yaw / contact-loss / landing state from here rather than re-deriving
/// it, and re-baseline the yaw reference at a phase transition via <see cref="ResetYawTracking"/>.
/// PURE MOVE: no behaviour change.
/// </summary>
public sealed class TelemetryAccumulator
{
	Vector3 _startPosM;
	Vector3 _lastPosM;
	Vector3 _prevVelMs;
	Vector3 _startFwd = Vector3.Forward;  // planar forward/left captured at run start
	Vector3 _startLeft = Vector3.Left;
	float _startHeadingDeg;
	float _prevHeadingDeg;
	float _yawAccumDeg;      // signed cumulative heading change (jturn/heading drift)
	float _maxYawAccumDeg;   // peak |cumulative yaw| during a run
	float _contactlessS;     // continuous seconds airborne AFTER first ground contact (edge detect)
	bool _everGrounded;      // has the car touched down yet? (gates spawn-settle vs real lift-off)

	int _ticks;
	int _airTicks;
	int _postContactTicks;    // ticks since first real ground contact — the contactLossPct denominator
	                          // (audit 2026-07-13 MEDIUM: _ticks includes the pre-ground motion-freeze,
	                          // which diluted the fraction; numerator _airTicks is already post-contact).
	int _wheelTicks;          // ticks counted into the per-wheel contact-loss average (post first contact)
	float _sumWheelLossFrac;  // per-tick fraction of wheels ungrounded, summed (wave-2: washboard bands)
	float _sumLatG, _sumYaw;
	float _minZM;            // for rollback / fallthrough

	bool _wasAirborne;
	bool _landed;
	float _landTime;
	float _settleStartTime;

	// ── reads the maneuver objects need ──
	public Vector3 StartPosM => _startPosM;
	public Vector3 StartFwd => _startFwd;
	public Vector3 StartLeft => _startLeft;
	public float YawAccumDeg => _yawAccumDeg;
	public float MaxYawAccumDeg => _maxYawAccumDeg;
	public float ContactlessS => _contactlessS;
	public bool Landed => _landed;
	public float LandTime => _landTime;
	public int Ticks => _ticks;

	/// <summary>Capture the run-start reference frame + zero the accumulators. Mirrors the exact
	/// resets the old StartManeuver did (note: _landTime/_settleStartTime are deliberately NOT reset
	/// — they only become meaningful after _landed latches, same as before).</summary>
	public void Start( VehicleController car, Rigidbody rb )
	{
		_ticks = 0;
		_airTicks = 0;
		_postContactTicks = 0;
		_wheelTicks = 0;
		_sumWheelLossFrac = 0f;
		_sumLatG = 0f;
		_sumYaw = 0f;
		_maxYawAccumDeg = 0f;
		_contactlessS = 0f;
		_everGrounded = false;
		_wasAirborne = false;
		_landed = false;

		_startPosM = car.WorldPosition * Units.UnitsToMeters;
		_lastPosM = _startPosM;
		_minZM = _startPosM.z;
		_prevVelMs = rb.IsValid() ? rb.Velocity * Units.UnitsToMeters : Vector3.Zero;

		var startRot = car.WorldRotation;
		_startFwd = startRot.Forward.WithZ( 0 ).Normal;
		_startLeft = startRot.Left.WithZ( 0 ).Normal;
		_startHeadingDeg = HeadingDeg( car );
		_prevHeadingDeg = _startHeadingDeg;
		_yawAccumDeg = 0f;
	}

	/// <summary>Re-baseline cumulative-yaw tracking at a maneuver phase transition (brake enters the
	/// braking phase; jturn enters the rotation phase). <paramref name="resetMax"/> also clears the
	/// running peak (jturn wants a fresh peak from rotation start; brake keeps its peak).</summary>
	public void ResetYawTracking( VehicleController car, bool resetMax )
	{
		_startHeadingDeg = HeadingDeg( car );
		_prevHeadingDeg = _startHeadingDeg;
		_yawAccumDeg = 0f;
		if ( resetMax )
			_maxYawAccumDeg = 0f;
	}

	public void Accumulate( VehicleController car, Rigidbody rb, float runTime, float dt )
	{
		_ticks++;

		var velMs = rb.Velocity * Units.UnitsToMeters;
		float speed = velMs.Length;
		var rot = car.WorldRotation;
		var fwd = rot.Forward;
		var right = rot.Right;

		// acceleration decomposed on the car's own axes -> long / lateral g
		var accelMs = (velMs - _prevVelMs) / MathF.Max( dt, 1e-4f );
		_prevVelMs = velMs;
		float longG = Vector3.Dot( accelMs, fwd ) / 9.81f;
		float latG = Vector3.Dot( accelMs, right ) / 9.81f;

		float yawRate = rb.AngularVelocity.z.RadianToDegree();
		var angles = rot.Angles();

		// grounded-wheel count drives both the top-speed guard and the airborne section below.
		int groundedWheels = car.Wheels.Count( w => w.IsGrounded );

		// distance travelled (planar)
		var posM = car.WorldPosition * Units.UnitsToMeters;
		VehicleBridge.DistanceM += (posM.WithZ( 0 ) - _lastPosM.WithZ( 0 )).Length;
		_lastPosM = posM;
		if ( posM.z < _minZM ) _minZM = posM.z;

		// heading unwrap for cumulative yaw (jturn / heading drift)
		float h = HeadingDeg( car );
		float dh = DeltaAngle( _prevHeadingDeg, h );
		_yawAccumDeg += dh;
		_maxYawAccumDeg = MathF.Max( _maxYawAccumDeg, MathF.Abs( _yawAccumDeg ) );
		_prevHeadingDeg = h;

		// live + peaks. maxSpeed is GROUNDED-ONLY (a plummet off the strip edge would corrupt it —
		// an early topspeed "165 m/s" defect was a free-fall off the +X ground edge, not a units bug).
		VehicleBridge.SpeedMs = speed;
		if ( groundedWheels >= 2 )
			VehicleBridge.MaxSpeedMs = MathF.Max( VehicleBridge.MaxSpeedMs, speed );
		VehicleBridge.Gear = car.Drivetrain?.Gear ?? 0;
		VehicleBridge.Rpm = car.Drivetrain?.Rpm ?? 0f;
		VehicleBridge.LongGPeak = MaxAbs( VehicleBridge.LongGPeak, longG );
		VehicleBridge.LateralGPeak = MaxAbs( VehicleBridge.LateralGPeak, latG );
		VehicleBridge.YawRatePeakDeg = MaxAbs( VehicleBridge.YawRatePeakDeg, yawRate );
		VehicleBridge.PitchDeg = MaxAbs( VehicleBridge.PitchDeg, angles.pitch );
		VehicleBridge.RollDeg = MaxAbs( VehicleBridge.RollDeg, angles.roll );

		_sumLatG += MathF.Abs( latG );
		_sumYaw += MathF.Abs( yawRate );

		// wheelspin: any driven wheel slipping hard under power
		bool spin = car.Throttle > 0.1f && car.Wheels.Any( w => w.IsDriven && w.IsGrounded && w.SlipRatio > 0.2f );
		if ( spin ) VehicleBridge.WheelspinS += dt;

		// lockup ticks under braking
		if ( car.Brake > 0.1f && car.Wheels.Any( w => w.IsGrounded && w.SlipRatio < -0.5f ) )
			VehicleBridge.LockupTicks++;

		// airtime / contact loss — ALL of it gated on first real ground contact (audit 2026-07-12
		// HIGH: _airTicks/_wasAirborne/AirtimeS previously accumulated during the 0.4 s spawn settle).
		bool airborne = groundedWheels == 0;
		if ( groundedWheels >= 1 ) _everGrounded = true;
		if ( _everGrounded ) _postContactTicks++; // contactLossPct denominator (post first contact only)
		_contactlessS = (airborne && _everGrounded) ? _contactlessS + dt : 0f;
		if ( airborne && _everGrounded ) _airTicks++;
		// per-wheel contact loss (wave-2): the washboard bands were authored against per-wheel
		// IsGrounded loss (handling-targets feel-heuristic 3), which full-airborne contactLossPct
		// cannot express — raycast wheels skipping ridges rarely put the WHOLE car airborne. Gated on
		// first real contact like the rest of the airtime family.
		int wheelCount = car.Wheels.Count();
		if ( _everGrounded && wheelCount > 0 )
		{
			_wheelTicks++;
			_sumWheelLossFrac += (wheelCount - groundedWheels) / (float)wheelCount;
		}
		// a REAL flight arms only after 0.15 s continuously airborne post-contact.
		if ( !_wasAirborne && _contactlessS > 0.15f )
		{
			_wasAirborne = true;
			VehicleBridge.AirtimeS = _contactlessS; // credit the arming window
		}
		else if ( airborne && _everGrounded && _wasAirborne )
		{
			VehicleBridge.AirtimeS += dt;
		}
		if ( !airborne && _wasAirborne && !_landed )
		{
			// first ground contact after a flight: capture landing attitude + start settle timer
			_landed = true;
			_landTime = runTime;
			VehicleBridge.LandingPitchDeg = MathF.Abs( angles.pitch );
			VehicleBridge.LandingRollDeg = MathF.Abs( angles.roll );
			_settleStartTime = runTime;
		}
		// settle: time from landing until angular velocity calms
		if ( _landed && VehicleBridge.SettleS == 0f )
		{
			if ( rb.AngularVelocity.Length.RadianToDegree() > 25f )
				_settleStartTime = runTime; // still moving; push the settle mark
			else if ( runTime - _settleStartTime > 0.3f )
				VehicleBridge.SettleS = _settleStartTime - _landTime + 0.3f;
		}
	}

	/// <summary>Per-run averages, computed once at FinishRun (mirrors the old FinishRun block).</summary>
	public void Finish()
	{
		if ( _ticks > 0 )
		{
			VehicleBridge.LateralGAvg = _sumLatG / _ticks;
			VehicleBridge.YawRateAvgDeg = _sumYaw / _ticks;
		}
		// contactLossPct: fraction of POST-FIRST-CONTACT ticks with all wheels off the ground. Both
		// numerator and denominator start at first ground contact, so the pre-ground motion-freeze no
		// longer dilutes it (audit 2026-07-13 MEDIUM; window documented in testing-harness §6.2).
		if ( _postContactTicks > 0 )
			VehicleBridge.ContactLossPct = 100f * _airTicks / _postContactTicks;
		if ( _wheelTicks > 0 )
			VehicleBridge.WheelContactLossPct = 100f * _sumWheelLossFrac / _wheelTicks;
	}

	public static float HeadingDeg( VehicleController car )
	{
		var f = car.WorldRotation.Forward;
		return MathF.Atan2( f.y, f.x ).RadianToDegree();
	}

	// running peak MAGNITUDE (non-negative): "peak" telemetry is asserted with <=, so a signed
	// value (e.g. pitch -20) would wrongly satisfy `pitchDeg <= 15`. Store |value|.
	static float MaxAbs( float cur, float candidate )
		=> MathF.Max( MathF.Abs( cur ), MathF.Abs( candidate ) );

	static float DeltaAngle( float from, float to )
	{
		float d = (to - from) % 360f;
		if ( d > 180f ) d -= 360f;
		if ( d < -180f ) d += 360f;
		return d;
	}
}
