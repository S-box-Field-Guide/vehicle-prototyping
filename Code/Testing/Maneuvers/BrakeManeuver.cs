namespace VehicleProto;

/// <summary>Accelerate to the entry speed, then full-brake to a stop. Measures the braking distance
/// (100-0) and the heading drift accumulated while braking.</summary>
public sealed class BrakeManeuver : ManeuverBase
{
	public override string Name => "brake";

	bool _brakePhase;
	float _brakeStartDistM;

	public override void Start( ManeuverContext ctx )
	{
		_brakePhase = false;
		_brakeStartDistM = 0f;
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float entry = ctx.Param( "entrySpeedMs", 27.78f );
		if ( !_brakePhase )
		{
			ctx.Drive( 1f, 0f, false );
			if ( VehicleBridge.SpeedMs >= entry )
			{
				_brakePhase = true;
				_brakeStartDistM = VehicleBridge.DistanceM;
				// re-baseline heading drift at brake onset (avoid a one-tick spurious delta), keep peak
				ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: false );
			}
			return false;
		}
		// full brake to a stop
		ctx.Drive( -1f, 0f, false );
		VehicleBridge.BrakeDistanceM = VehicleBridge.DistanceM - _brakeStartDistM;
		VehicleBridge.HeadingDriftDeg = MathF.Abs( ctx.Telemetry.YawAccumDeg );
		return VehicleBridge.SpeedMs < 0.5f;
	}

	public override string TimingValue( ManeuverContext ctx )
		=> _brakePhase
			? MathF.Max( 0f, VehicleBridge.DistanceM - _brakeStartDistM ).ToString( "F1" )
			: "—";

	public override void Report( RunVerdict v )
	{
		v.Add( $"brakeDistanceM={VehicleBridge.BrakeDistanceM:F1}", "Brake distance", $"{VehicleBridge.BrakeDistanceM:F1} m" );
		v.Add( $"headingDriftDeg={VehicleBridge.HeadingDriftDeg:F1}", "Heading drift", $"{VehicleBridge.HeadingDriftDeg:F1}°" );
		v.Add( $"lockupTicks={VehicleBridge.LockupTicks}", "Lockup ticks", $"{VehicleBridge.LockupTicks}" );
	}
}
