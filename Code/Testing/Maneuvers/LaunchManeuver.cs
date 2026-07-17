namespace VehicleProto;

/// <summary>Standing-start acceleration: full throttle, record time to the split speed
/// (<c>zeroToHundredS</c>, default 100 km/h) and end at the target speed — or on an accel plateau
/// for a car whose top speed is below the split (the kart, measured on a 0-50 km/h split).</summary>
public sealed class LaunchManeuver : ManeuverBase
{
	public override string Name => "launch";

	bool _reached100;

	public override void Start( ManeuverContext ctx )
	{
		_reached100 = false;
		ResetPlateau();
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float target = ctx.Param( "targetSpeedMs", 27.78f );
		// Launch split speed for zeroToHundredS. Default 100 km/h (27.78 m/s). A car whose top speed
		// is below 100 km/h (the kart) sets splitSpeedMs to a reachable split (e.g. 50 km/h) so its
		// launch metric is physical rather than a permanent 0 (kart-launch band revised to 0-50 during tuning).
		float split = ctx.Param( "splitSpeedMs", 27.78f );
		ctx.Drive( 1f, 0f, false );
		if ( !_reached100 && VehicleBridge.SpeedMs >= split )
		{
			_reached100 = true;
			VehicleBridge.ZeroToHundredS = ctx.RunTime;
		}
		if ( VehicleBridge.SpeedMs >= target && _reached100 )
			return true;
		// below-split car (kart): end once it plateaus instead of idling at full throttle to maxRunS.
		return ctx.RunTime > 6f && AccelStalled( ctx.RunTime );
	}

	public override string TimingValue( ManeuverContext ctx ) => ctx.RunTime.ToString( "F2" );

	public override void Report( RunVerdict v )
	{
		v.Add( $"zeroToHundredS={VehicleBridge.ZeroToHundredS:F2}", "0-100 km/h", $"{VehicleBridge.ZeroToHundredS:F2} s" );
		v.Add( $"wheelspinS={VehicleBridge.WheelspinS:F2}", "Wheelspin", $"{VehicleBridge.WheelspinS:F2} s" );
		v.Add( $"pitchDeg={VehicleBridge.PitchDeg:F1}", "Pitch peak", $"{VehicleBridge.PitchDeg:F1}°" );
	}
}
