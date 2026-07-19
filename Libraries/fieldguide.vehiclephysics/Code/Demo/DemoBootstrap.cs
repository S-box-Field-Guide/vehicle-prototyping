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

	/// <summary>Cars falling below this world height (metres) are placed back on their spawn spot.</summary>
	[Property] public float VoidResetHeightM { get; set; } = -20f;

	readonly List<VehicleController> _cars = new();
	readonly List<Vector3> _spawns = new();
	VehicleController _active;

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

		_cars.Clear();
		_spawns.Clear();
		for ( int i = 0; i < roster.Length; i++ )
		{
			var def = roster[i];
			float seatZ = VehicleFactory.SeatHeightM( def );
			var pos = new Vector3( 0f, startY + i * SpacingMeters, seatZ ) * m;

			var go = VehicleFactory.Spawn( Scene, def, pos, Rotation.Identity );
			var controller = go.Components.Get<VehicleController>();
			if ( controller is not null )
			{
				_cars.Add( controller );
				_spawns.Add( pos );
			}
		}

		_active = _cars.FirstOrDefault();

		// Point the scene's chase camera at the active car so the demo frames it on load.
		var cam = Scene.GetAllComponents<VehicleCamera>().FirstOrDefault();
		if ( cam is not null )
			cam.Target = _active;
	}

	protected override void OnFixedUpdate()
	{
		float m = Units.MetersToUnits;

		for ( int i = 0; i < _cars.Count; i++ )
		{
			var car = _cars[i];
			if ( car is null || !car.IsValid() )
				continue;

			// Only the camera's car listens to the player; parked cars hold neutral inputs.
			// (With no override every controller samples the same keyboard, so one W press
			// would launch the whole row at once.)
			car.InputOverride = car == _active ? null : default( DriveInputs );

			// Void watchdog: driving off the pad edge otherwise means falling forever.
			if ( car.GameObject.WorldPosition.z < VoidResetHeightM * m )
			{
				car.GameObject.WorldPosition = _spawns[i];
				car.GameObject.WorldRotation = Rotation.Identity;
				var body = car.GameObject.Components.Get<Rigidbody>();
				if ( body is not null )
				{
					body.Velocity = Vector3.Zero;
					body.AngularVelocity = Vector3.Zero;
				}
			}
		}
	}
}
