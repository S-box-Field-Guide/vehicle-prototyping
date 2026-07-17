namespace VehicleProto;

/// <summary>Waypoint-route stub. Route driving isn't implemented yet — this seats the car (the
/// pilot's Route op spawns it at the station) and finishes immediately with the stub message so the
/// runner never hangs. It exists as a first-class maneuver object so the real waypoint follower drops
/// in here (new Tick body) without touching the pilot or the registry contract.</summary>
public sealed class RouteManeuver : ManeuverBase
{
	public const string StubMessage = "route stub (M0): no waypoint follower yet";

	public override string Name => "route";

	public override void Start( ManeuverContext ctx ) { }

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		VehicleBridge.Message = StubMessage;
		return true; // no waypoint follower yet — finish right away
	}

	// no measured metrics yet: RunVerdict.Build falls back to the default elapsedS row.
	public override void Report( RunVerdict v ) { }
}
