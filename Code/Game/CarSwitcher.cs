namespace VehicleProto;

/// <summary>
/// Live car-swap for player-facing driving (the Tab-menu "Change vehicle" was a
/// stub). Despawns the current car and spawns the chosen roster car AT THE CURRENT ground
/// position + heading (not back at a station), then rewires every reference that pointed at the
/// old car: the pilot's <see cref="VehiclePilot.ActiveCar"/>, the chase camera, the whole HUD
/// stack (<see cref="UiRig.Retarget"/>), and <see cref="VehicleBridge.SpawnedCar"/>.
///
/// It deliberately reuses the harness spawn path rather than duplicating it: geometry comes from
/// <see cref="VehicleFactory.Spawn"/> and the seat height from <see cref="VehiclePilot.SeatHeightM"/>
/// (the ONE suspension-equilibrium formula — spawning at surface+radius flings the car).
/// Car ids resolve through <see cref="VehiclePilot.ResolveCar"/>, the canonical alias table.
///
/// Entry points: the SessionMenu picker (cycles <see cref="Roster"/>) and the <c>vp_car &lt;id&gt;</c>
/// console command for quick testing.
/// </summary>
public static class CarSwitcher
{
	/// <summary>Player-facing roster, in cycle order (hatch → coupe → kart → pickup). The separate
	/// "hatchkit" slot was collapsed into "hatch" (de-Kenney sign-off 2026-07-13): hatch now spawns
	/// the hatch_kit part kit directly, so a dedicated kit slot is redundant. The console command
	/// accepts any id <see cref="VehiclePilot.ResolveCar"/> knows.</summary>
	public static readonly string[] Roster = { "hatch", "coupe", "kart", "pickup" };

	/// <summary>
	/// Swap the active car to <paramref name="carId"/>, keeping the current ground XY + heading and
	/// the active drive mode (assist level). Returns the new controller (null only if there is no scene).
	/// </summary>
	public static VehicleController SwitchTo( Scene scene, string carId, VehiclePilot pilot = null )
	{
		if ( scene is null )
			return null;

		carId = (carId ?? "").Trim().ToLowerInvariant();
		var def = VehiclePilot.ResolveCar( carId ); // unknown id → hatch (canonical table)

		pilot ??= scene.GetAllComponents<VehiclePilot>().FirstOrDefault();
		var current = pilot?.ActiveCar;
		if ( !current.IsValid() )
			current = scene.GetAllComponents<VehicleController>().FirstOrDefault();

		// preserve where the outgoing car sits: recover the GROUND height under it (chassis center
		// minus its own suspension-equilibrium seat height), keep XY, and flatten rotation to a pure
		// heading so the new car never inherits a tilt/roll. Also carry the ACTIVE drive mode (assist
		// level) forward — the outgoing car IS the live session mode holder (the SessionMenu segbar
		// writes straight onto Target.Assists), so switching keeps you in Sport/Sim/Casual instead of
		// snapping back to the incoming def's DefaultAssists (spawn resets it in OnAwake). null = no
		// outgoing car (initial spawn) → the new car keeps its own default.
		Vector3 posM = Vector3.Zero;
		Rotation facing = Rotation.Identity;
		AssistLevel? carryMode = null;
		if ( current.IsValid() )
		{
			var curPosM = current.WorldPosition * Units.UnitsToMeters;
			float groundZM = curPosM.z - VehiclePilot.SeatHeightM( current.Definition );
			posM = curPosM.WithZ( groundZM );

			var fwd = current.WorldRotation.Forward.WithZ( 0f );
			facing = fwd.IsNearZeroLength ? current.WorldRotation : Rotation.LookAt( fwd.Normal, Vector3.Up );

			carryMode = current.Assists;
		}

		// re-seat the incoming def at ITS own equilibrium height above that same ground
		float m = Units.MetersToUnits;
		float seatZM = VehiclePilot.SeatHeightM( def );
		var pos = posM.WithZ( posM.z + seatZM ) * m;

		if ( current.IsValid() )
			current.GameObject.Destroy();

		var carGo = VehicleFactory.Spawn( scene, def, pos, facing );
		var controller = carGo.Components.Get<VehicleController>();

		// re-apply the carried drive mode via the same single assignment the segbar click uses
		// (Assists is a plain live-read flag — setting it IS the whole apply path, no side effects to
		// duplicate). Skipped when there was no outgoing car, so the initial spawn keeps its default.
		if ( carryMode.HasValue && controller.IsValid() )
			controller.Assists = carryMode.Value;

		// rewire every reference that pointed at the old (now-destroyed) car
		if ( pilot is not null )
			pilot.ActiveCar = controller;
		VehicleBridge.SpawnedCar = carId;

		var chase = scene.Camera?.GameObject?.Components.Get<VehicleCamera>();
		if ( chase is not null )
			chase.Target = controller;

		UiRig.Retarget( scene, controller );

		Log.Info( $"[vp] car switch -> {carId} at ({posM.x:F0},{posM.y:F0}) seatZ={seatZM:F2}m [in place]" );
		return controller;
	}

	/// <summary>The <see cref="Roster"/> id whose definition matches <paramref name="car"/> (by
	/// Definition.Name — all five roster names are distinct); falls back to "hatch".</summary>
	public static string CurrentId( VehicleController car )
	{
		var name = car?.Definition?.Name;
		if ( !string.IsNullOrEmpty( name ) )
			foreach ( var id in Roster )
				if ( VehiclePilot.ResolveCar( id ).Name == name )
					return id;
		return "hatch";
	}

	/// <summary>Next car in the cycle after whatever is currently active.</summary>
	public static string NextId( VehicleController car )
	{
		int idx = System.Array.IndexOf( Roster, CurrentId( car ) );
		return Roster[(idx + 1) % Roster.Length];
	}

	[ConCmd( "vp_car" )]
	public static void VpCar( string car = "hatch" )
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
		{
			Log.Warning( "[vp] vp_car: no active scene (play mode not running?)" );
			return;
		}
		SwitchTo( scene, car );
	}
}
