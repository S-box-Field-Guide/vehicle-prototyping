namespace VehicleProto;

/// <summary>
/// Diagnostic-only (NOT part of any battery spec): park the active car on the north perimeter ring
/// road a few metres south of the city's north hedge — a long colliding wall (CityBuilder "Hedge N")
/// — oriented at a chosen incidence, so a following straight-throttle maneuver (e.g.
/// <c>vp_drive {"op":"maneuver","maneuver":"topspeed"}</c> with NO station, which drives the current
/// car in place) runs it INTO the wall at a known angle. Used to verify VehicleController's
/// wall-glance forgiveness assist: shallow angles should log <c>[vp] wallglance</c> and preserve
/// speed; near head-on (>= WallGlanceHeadOnDeg) should stay a hard stop.
///
/// Usage: <c>vp_wallpose [approachDeg]</c> — approachDeg is the angle of travel from the wall
/// tangent AND equals the incidence the assist sees (~20 = shallow graze → full assist; ~80 = near
/// head-on → no assist). Default 20.
/// </summary>
public static class WallGrazeProbe
{
	[ConCmd( "vp_wallpose" )]
	public static void WallPose( float approachDeg = 20f )
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
		{
			Log.Warning( "[vp] vp_wallpose: no active scene (play mode not running?)" );
			return;
		}

		var pilot = scene.GetAllComponents<VehiclePilot>().FirstOrDefault();
		var controller = pilot?.ActiveCar;
		if ( !controller.IsValid() )
			controller = scene.GetAllComponents<VehicleController>().FirstOrDefault();
		if ( !controller.IsValid() )
		{
			Log.Warning( "[vp] vp_wallpose: no active car to pose" );
			return;
		}

		float m = Units.MetersToUnits;
		// north hedge wall plane, derived from CityBuilder constants (drift-safe): the perimeter
		// hedge sits just outside the outermost ring road, which is clear of buildings full-width.
		float hedgeY = -CityBuilder.Origin + CityBuilder.RoadWidth * 0.5f + 2f;
		float startY = hedgeY - 9f;  // on the ring road, 9 m south of the wall
		float startX = -30f;         // room to run east along the ring road on a shallow graze
		float seatZ = VehiclePilot.SeatHeightM( controller.Definition );

		float deg = Math.Clamp( approachDeg, 0f, 90f );
		var rb = controller.Components.Get<Rigidbody>();
		controller.WorldPosition = new Vector3( startX, startY, seatZ ) * m;
		controller.WorldRotation = Rotation.FromYaw( deg ); // +yaw = CCW toward +Y = into the north hedge; yaw == incidence
		if ( rb.IsValid() )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}

		Log.Info( $"[vp] wallpose approachDeg={deg:F0} at ({startX:F0},{startY:F0}) → hedge Y={hedgeY:F0}. Now run: vp_drive topspeed (no station) to graze." );
	}
}
