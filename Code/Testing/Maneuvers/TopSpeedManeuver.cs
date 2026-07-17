namespace VehicleProto;

/// <summary>Flat-out run: full throttle to terminal velocity. <c>maxSpeedMs</c> is grounded-only
/// (banked in the accumulator); the run ends when the car runs off the finite runway edge
/// (contact-loss &gt; 0.3 s) or the acceleration plateaus.</summary>
public sealed class TopSpeedManeuver : ManeuverBase
{
	public override string Name => "topspeed";

	public override void Start( ManeuverContext ctx ) => ResetPlateau();

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		ctx.Drive( 1f, 0f, false );
		// record the gear at the current (grounded) max-speed sample
		if ( VehicleBridge.SpeedMs >= VehicleBridge.MaxSpeedMs - 0.05f )
			VehicleBridge.GearAtVmax = ctx.Car.Drivetrain?.Gear ?? 0;
		// ran off the end of the finite dragstrip runway: stop before the car free-falls (maxSpeedMs is
		// grounded-only so the peak GROUND speed is already banked). 0.3 s of air is past any bump.
		if ( ctx.Telemetry.ContactlessS > 0.3f )
			return true;
		// topped out when speed climbs < 0.5 m/s over a ~2 s window (after an initial spool-up)
		return ctx.RunTime > 8f && AccelStalled( ctx.RunTime );
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"maxSpeedMs={VehicleBridge.MaxSpeedMs:F1}", "Top speed", $"{VehicleBridge.MaxSpeedMs * 3.6f:F1} km/h" );
		v.Add( $"gearAtVmax={VehicleBridge.GearAtVmax}", "Gear @ vmax", $"{VehicleBridge.GearAtVmax}" );
	}
}
