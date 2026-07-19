namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Minimal demo bootstrap for the Vehicle Physics Kit's demo scene. Spawns one blockout car per
/// <see cref="CarDefinitions"/> roster entry in a row on the flat pad, and points the scene's
/// <see cref="VehicleCamera"/> at the first one. Proves the kit drives in isolation; not part of the
/// consumer surface (the demo scene is the only thing that references it).
/// </summary>
public sealed class DemoBootstrap : Component
{
	/// <summary>Spacing between spawned cars along the row (metres).</summary>
	[Property] public float SpacingMeters { get; set; } = 5f;

	protected override void OnStart()
	{
		// ~1.1 g like the source proving ground, so the tuned handling feels right. Read live by the
		// factory's static-load and seat-height math, so setting it here keeps every car level on spawn.
		if ( Scene.PhysicsWorld is not null )
			Scene.PhysicsWorld.Gravity = Vector3.Down * 9.81f * 1.1f * Units.MetersToUnits;

		var roster = new[]
		{
			CarDefinitions.Hatch,
			CarDefinitions.Coupe,
			CarDefinitions.Kart,
			CarDefinitions.Pickup,
		};

		float m = Units.MetersToUnits;
		float startY = -(roster.Length - 1) * SpacingMeters * 0.5f;

		VehicleController first = null;
		for ( int i = 0; i < roster.Length; i++ )
		{
			var def = roster[i];
			float seatZ = VehicleFactory.SeatHeightM( def );
			var pos = new Vector3( 0f, startY + i * SpacingMeters, seatZ ) * m;

			var go = VehicleFactory.Spawn( Scene, def, pos, Rotation.Identity );
			first ??= go.Components.Get<VehicleController>();
		}

		// Point the scene's chase camera at the first car so the demo frames it on load.
		var cam = Scene.GetAllComponents<VehicleCamera>().FirstOrDefault();
		if ( cam is not null )
			cam.Target = first;
	}
}
