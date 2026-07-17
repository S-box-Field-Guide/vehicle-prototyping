namespace VehicleProto;

/// <summary>Handbrake 180° then a catch. Layout-aware rotation phase (FWD pulls the nose round on a
/// held handbrake; RWD does a brief handbrake initiation then power-oversteers), timed to 180°, then
/// a settle phase that counter-steers the residual yaw and checks the slide is catchable (yaw falls
/// under threshold without spinning past 220°).</summary>
public sealed class JTurnManeuver : ManeuverBase
{
	public override string Name => "jturn";

	int _phase;
	float _phaseStartTime;

	public override void Start( ManeuverContext ctx )
	{
		_phase = 0;
		_phaseStartTime = 0f;
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		// entry ~60 km/h (disturbance-onset speed); settle threshold + window are the class rows.
		float entry = ctx.Param( "entrySpeedMs", 16.7f );
		float rotThrottle = ctx.Param( "rotThrottleFrac", 0.6f );
		float settleYaw = ctx.Param( "settleYawDeg", 15f );
		float settleWin = ctx.Param( "settleWindowS", 1.2f );

		if ( _phase == 0 )
		{
			ctx.Drive( 1f, 0f, false );
			if ( VehicleBridge.SpeedMs >= entry )
			{
				_phase = 1;
				_phaseStartTime = ctx.RunTime;
				ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: true );
			}
			return false;
		}
		if ( _phase == 1 )
		{
			// Rotation phase is LAYOUT-AWARE (driven wheels differ, so the same input does opposite
			// things). FWD: handbrake locks the undriven rears loose while full lock + sustained
			// throttle keep the driven FRONTS pulling the nose around. RWD/AWD: brief handbrake to
			// break the rear loose, then RELEASE and power-oversteer around on full throttle + lock.
			bool fwd = ctx.Car.Definition?.Layout == DriveLayout.FWD;
			if ( fwd )
			{
				ctx.Drive( rotThrottle, 1f, true );
			}
			else
			{
				bool initiating = (ctx.RunTime - _phaseStartTime) < ctx.Param( "hbInitiateS", 0.35f );
				ctx.Drive( initiating ? 0f : 1f, 1f, initiating );
			}
			float turned = MathF.Abs( ctx.Telemetry.YawAccumDeg );
			if ( turned >= 180f && VehicleBridge.JturnTimeS == 0f )
			{
				VehicleBridge.JturnTimeS = ctx.RunTime - _phaseStartTime;
				_phase = 2;
				_phaseStartTime = ctx.RunTime;
			}
			// safety: never let the rotation phase run forever if the car bogs down
			return ctx.RunTime - _phaseStartTime > 6f;
		}

		// settle phase: release handbrake, counter-steer against the residual yaw, no throttle —
		// measure whether the slide is CATCHABLE (yaw falls under X deg/s and never spun past 220°).
		float yawRateNow = ctx.Body.AngularVelocity.z.RadianToDegree();
		ctx.Drive( 0f, -MathF.Sign( yawRateNow ) * 0.5f, false );
		VehicleBridge.YawOvershootDeg = MathF.Max( 0f, ctx.Telemetry.MaxYawAccumDeg - 180f );
		bool spunPast220 = ctx.Telemetry.MaxYawAccumDeg > 220f;
		if ( !spunPast220 && MathF.Abs( yawRateNow ) < settleYaw )
			VehicleBridge.Catchable = true;
		return ctx.RunTime - _phaseStartTime > settleWin;
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"jturnTimeS={VehicleBridge.JturnTimeS:F2}", "J-turn time", $"{VehicleBridge.JturnTimeS:F2} s" );
		v.Add( $"yawOvershootDeg={VehicleBridge.YawOvershootDeg:F1}", "Yaw overshoot", $"{VehicleBridge.YawOvershootDeg:F1}°" );
		v.Add( $"catchable={VehicleBridge.Catchable}", "Catchable", VehicleBridge.Catchable ? "yes" : "no" );
		v.YawSummary = VehicleBridge.Catchable ? "catchable" : "spun";
	}
}
