namespace VehicleProto;

/// <summary>Build speed, then hold a constant right-hand steer to circle at the grip limit; the
/// accumulator reads out the steady-state lateral g and yaw rate. Runs to maxRunS.</summary>
public sealed class SkidpadManeuver : ManeuverBase
{
	public override string Name => "skidpad";

	public override void Start( ManeuverContext ctx ) { }

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float steer = -0.55f; // negative = right (Rotation.FromYaw(+) is LEFT)
		if ( ctx.RunTime < 2.5f )
			ctx.Drive( 0.7f, steer * (ctx.RunTime / 2.5f), false );
		else
			ctx.Drive( 0.45f, steer, false );

		// spin-out detection: yaw rate runs away
		if ( MathF.Abs( ctx.Body.AngularVelocity.z.RadianToDegree() ) > 150f )
			VehicleBridge.SpunOut = true;

		return false; // runs until maxRunS, accumulating steady-state averages
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"lateralGAvg={VehicleBridge.LateralGAvg:F2}", "Lateral g avg", $"{VehicleBridge.LateralGAvg:F2} g" );
		v.Add( $"yawRateAvgDeg={VehicleBridge.YawRateAvgDeg:F1}", "Yaw rate avg", $"{VehicleBridge.YawRateAvgDeg:F1} deg/s" );
		v.Add( $"spunOut={VehicleBridge.SpunOut}", "Spun out", VehicleBridge.SpunOut ? "yes" : "no" );
	}
}
