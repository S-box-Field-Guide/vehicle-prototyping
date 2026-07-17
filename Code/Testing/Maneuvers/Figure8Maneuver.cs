namespace VehicleProto;

/// <summary>
/// Figure-8 (paired skidpad circles). Like <see cref="SkidpadManeuver"/> but reverses
/// the steering each lobe, so the car circles one way, transitions through neutral (the combined-slip
/// direction change the maneuver exists to exercise), then circles the other way. The accumulator reads
/// out steady-state <c>lateralGAvg</c> / <c>yawRateAvgDeg</c> over the whole run; <c>lateralGPeak</c> is
/// the lobe-consistency proxy (frozen contract has no per-lobe field); <c>spunOut</c> latches on a yaw
/// runaway, exactly as skidpad.
///
/// A run's assist level may be pinned per spec (params.assist) — the pilot re-applies that pin every
/// tick (docs/testing-harness.md §7.1); this maneuver only stages steer/throttle. Determinism law: no RNG.
/// </summary>
public sealed class Figure8Maneuver : ManeuverBase
{
	public override string Name => "figure8";

	int _lobe;
	int _sign;

	public override void Start( ManeuverContext ctx )
	{
		_lobe = 0;
		_sign = -1; // first lobe turns right (negative steer), matching skidpad's default hand
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		int lobes = (int)MathF.Round( ctx.Param( "lobes", 2f ) );
		float steer = _sign * 0.55f;

		// ramp steer in over the first 2.5 s (skidpad's entry transient), then hold the circle.
		if ( ctx.RunTime < 2.5f )
			ctx.Drive( 0.7f, steer * (ctx.RunTime / 2.5f), false );
		else
			ctx.Drive( 0.45f, steer, false );

		// spin-out detection: yaw rate runs away (same threshold as skidpad).
		if ( MathF.Abs( ctx.Body.AngularVelocity.z.RadianToDegree() ) > 150f )
			VehicleBridge.SpunOut = true;

		// each lobe ~ one full circle; when this lobe's accumulated |yaw| nears 360°, flip the hand and
		// re-baseline the yaw tracking for the next lobe (keep the running peak — resetMax:false).
		if ( MathF.Abs( ctx.Telemetry.YawAccumDeg ) >= 330f )
		{
			_lobe++;
			_sign = -_sign;
			ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: false );
		}

		return _lobe >= lobes; // done once both lobes are complete (else ends on maxRunS = a DNF)
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"lateralGAvg={VehicleBridge.LateralGAvg:F2}", "Lateral g avg", $"{VehicleBridge.LateralGAvg:F2} g" );
		v.Add( $"lateralGPeak={VehicleBridge.LateralGPeak:F2}", "Lateral g peak", $"{VehicleBridge.LateralGPeak:F2} g" );
		v.Add( $"yawRateAvgDeg={VehicleBridge.YawRateAvgDeg:F1}", "Yaw rate avg", $"{VehicleBridge.YawRateAvgDeg:F1} deg/s" );
		v.Add( $"spunOut={VehicleBridge.SpunOut}", "Spun out", VehicleBridge.SpunOut ? "yes" : "no" );
	}
}
