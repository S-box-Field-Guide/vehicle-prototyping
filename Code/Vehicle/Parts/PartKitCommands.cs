namespace VehicleProto;

/// <summary>
/// Console on-ramp for spawning a part-kit car directly (dev harness). Since the roster carries the
/// kits natively now (hatch = hatch_kit, coupe/kart/pickup = their kits), this is mainly a quick
/// station-spawn helper.
///
/// Usage (console or MCP console tool):  <c>vp_spawn_kit [station] [car]</c>  (default
/// dragstrip / hatch; car also accepts pickup / coupe / kart).
/// It seats the kit car exactly like the pilot's own SpawnCarAt (same station registry, same
/// suspension-equilibrium seat height), hands it to the pilot via the public
/// <see cref="VehiclePilot.ActiveCar"/> property, and rebinds the chase camera. A following
/// <c>vp_drive {"op":"maneuver","maneuver":"launch"}</c> WITHOUT a station then runs the
/// maneuver on this car in place (StartManeuver only respawns when a station is given, and
/// rebinds its rigidbody from Active at run start).
/// </summary>
public static class PartKitCommands
{
	[ConCmd( "vp_spawn_kit" )]
	public static void SpawnKit( string station = "dragstrip", string car = "hatch" )
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
		{
			Log.Warning( "[vp] vp_spawn_kit: no active scene (play mode not running?)" );
			return;
		}

		var carId = string.IsNullOrWhiteSpace( car ) ? "hatch" : car.ToLowerInvariant();
		CarDefinition def = carId switch
		{
			// de-Kenney collapse 2026-07-13: "hatch" is the hatch_kit part kit now (was "hatchkit").
			"hatch" => CarDefinitions.Hatch,
			"pickup" => CarDefinitions.Pickup,
			"coupe" => CarDefinitions.Coupe,
			"kart" => CarDefinitions.Kart,
			_ => null,
		};
		if ( def is null )
		{
			Log.Warning( $"[vp] vp_spawn_kit: unknown kit car '{car}' (known: hatch, pickup, coupe, kart) — defaulting to hatch" );
			carId = "hatch";
			def = CarDefinitions.Hatch;
		}

		if ( string.IsNullOrWhiteSpace( station ) )
			station = "dragstrip";

		var pilot = scene.GetAllComponents<VehiclePilot>().FirstOrDefault();

		Vector3 posM;
		Rotation facing;
		if ( !VehiclePilot.ResolveStation( station, out posM, out facing ) )
		{
			Log.Warning( $"[vp] vp_spawn_kit: unknown station '{station}' — spawning at origin" );
			posM = Vector3.Zero;
			facing = Rotation.Identity;
		}

		if ( pilot is not null && pilot.ActiveCar.IsValid() )
			pilot.ActiveCar.GameObject.Destroy();

		float m = Units.MetersToUnits;
		float seatZM = VehiclePilot.SeatHeightM( def );
		var pos = posM * m + Vector3.Up * seatZM * m;

		var carGo = PartKitFactory.Spawn( scene, def, pos, facing );
		var controller = carGo.Components.Get<VehicleController>();

		if ( pilot is not null )
		{
			pilot.ActiveCar = controller;
			VehicleBridge.SpawnedCar = carId;
		}

		var chase = scene.Camera?.GameObject?.Components.Get<VehicleCamera>();
		if ( chase is not null )
			chase.Target = controller;

		Log.Info( $"[vp] spawn car={carId} station={station} seatZ={seatZM:F2}m at ({posM.x:F0},{posM.y:F0}) [partkit]" );
	}

	// There are no vp_damage / vp_repair commands here: runtime deformation (an impact router with a
	// dent-hash readout and repair) is out of scope for this prototyping kit. The manifest keeps only
	// inert damage bands (data, no deformation), so there is nothing to report on or repair.
}
