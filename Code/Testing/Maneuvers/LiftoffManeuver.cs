namespace VehicleProto;

/// <summary>
/// Lift-off oversteer probe (docs/handling-targets.md "Lift-off oversteer" rows + the Catchability
/// feel-heuristic). Enter a corner at speed, then mid-corner LIFT the throttle AND release the
/// steering to neutral — the disturbance a driver feels as the rear stepping out. The rotation the
/// car keeps carrying PAST the lift-instant heading is the oversteer overshoot: an understeer-biased
/// FWD car straightens fast (small overshoot); a power-down RWD car keeps rotating (larger overshoot).
///
/// Phases:
///   0. APPROACH — full throttle on a light steer to load the outside tyres, until entrySpeedMs.
///   1. CORNER   — steady steer at maintenance throttle to establish a real cornering state, until
///      BOTH the scripted apex (liftoffApexFrac × maxRunS) AND a fixed corner-establishment window
///      after entry have passed (so a car that reaches entry speed late still corners before lifting).
///   2. LIFT-OFF — throttle cut to 0, steering released to 0. Yaw tracking is re-baselined at this
///      instant (resetMax) so <c>yawOvershootDeg</c> = the peak |yaw| deviation FROM the lift heading
///      (the "peak yaw deviation from straight during the disturbance" reading of the shared field —
///      NOT the jturn past-180° reading). SpunOut latches on a runaway (past 90° / &gt;150 deg/s).
///
/// Never applies forces — only stages <see cref="DriveInputs"/>; determinism law: no RNG.
/// </summary>
public sealed class LiftoffManeuver : ManeuverBase
{
	public override string Name => "liftoff";

	int _phase;
	float _liftTime;
	float _cornerStartTime;

	public override void Start( ManeuverContext ctx )
	{
		_phase = 0;
		_liftTime = 0f;
		_cornerStartTime = 0f;
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float entry = ctx.Param( "entrySpeedMs", 22f );
		float apexFrac = ctx.Param( "liftoffApexFrac", 0.5f );
		float settleWin = ctx.Param( "settleWindowS", 1.0f );
		float maxRun = ctx.Param( "maxRunS", 10f );

		// The lift fires once BOTH gates pass: (a) the scripted apex time (apexFrac × maxRunS — the
		// spec's authored lift point), and (b) a fixed corner-establishment window after entry speed
		// (probe finding 2026-07-13: a slower car reaches entry speed AFTER the apex time, which
		// collapsed phase 1 to one tick — the car lifted with no loaded corner, lateralGPeak 0.32 g).
		const float cornerEstablishS = 1.5f;
		float liftAt = apexFrac * maxRun;

		// negative steer = right (Rotation.FromYaw(+) is LEFT), same convention as SkidpadManeuver.
		const float corner = -0.5f;

		if ( _phase == 0 )
		{
			ctx.Drive( 1f, corner * 0.3f, false ); // gentle load onto the outside tyres while building speed
			if ( VehicleBridge.SpeedMs >= entry && ctx.RunTime >= 1.0f )
			{
				_phase = 1;
				_cornerStartTime = ctx.RunTime;
			}
			return false;
		}
		if ( _phase == 1 )
		{
			ctx.Drive( 0.4f, corner, false ); // steady cornering to reach a real turned-in state
			if ( ctx.RunTime >= liftAt && ctx.RunTime - _cornerStartTime >= cornerEstablishS )
			{
				_phase = 2;
				_liftTime = ctx.RunTime;
				// overshoot is measured FROM the lift instant, not from run start.
				ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: true );
			}
			return false;
		}

		// LIFT-OFF disturbance: cut throttle, release steering. Residual rotation = the oversteer.
		ctx.Drive( 0f, 0f, false );
		VehicleBridge.YawOvershootDeg = ctx.Telemetry.MaxYawAccumDeg;
		float yawRateNow = ctx.Body.AngularVelocity.z.RadianToDegree();
		if ( MathF.Abs( yawRateNow ) > 150f || ctx.Telemetry.MaxYawAccumDeg > 90f )
			VehicleBridge.SpunOut = true;

		// give the disturbance time to peak + settle before ending (still capped by maxRunS).
		return ctx.RunTime - _liftTime > MathF.Max( settleWin, 2f );
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"yawOvershootDeg={VehicleBridge.YawOvershootDeg:F1}", "Yaw overshoot", $"{VehicleBridge.YawOvershootDeg:F1}°" );
		v.Add( $"lateralGPeak={VehicleBridge.LateralGPeak:F2}", "Lateral g peak", $"{VehicleBridge.LateralGPeak:F2} g" );
		v.Add( $"spunOut={VehicleBridge.SpunOut}", "Spun out", VehicleBridge.SpunOut ? "yes" : "no" );
		v.YawSummary = VehicleBridge.SpunOut ? "spun" : "held";
	}
}
