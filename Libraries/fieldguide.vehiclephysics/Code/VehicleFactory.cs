namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Builds a drivable blockout car from a CarDefinition at runtime: box body, sphere wheels
/// (30-minute blockouts that genuinely fit the art style). No prefab assets needed; swap to real
/// models later without touching physics via the <see cref="CustomBodyBuilder"/> seam.
/// </summary>
public static class VehicleFactory
{
	/// <summary>
	/// Optional custom body builder seam (spec 3.1). When set, it is called with
	/// (scene, carRoot, def) to build a custom VISUAL body under the car root; return true if it built
	/// successfully. Null (default) or a false return uses the primitive blockout body path. Physics,
	/// raycast wheels, wheel visuals, the citizen driver, and audio are always factory-built regardless
	/// of the body path — the seam swaps only the visual body, so a consumer (for example a part-kit
	/// assembler) can drop real models in without touching physics. The builder may read
	/// <see cref="CarDefinition.BodyManifest"/> to decide what to assemble.
	/// </summary>
	public static Func<Scene, GameObject, CarDefinition, bool> CustomBodyBuilder { get; set; }

	// Default citizen driver seat point in the car-root frame (metres), used by the blockout kart body
	// and by a custom body that opts into the shared driver. A custom builder may seat its own driver.
	static readonly Vector3 DefaultDriverLocalM = new( 0.05f, 0f, 0.06f );

	public static GameObject Spawn( Scene scene, CarDefinition def, Vector3 position, Rotation rotation )
	{
		float m = Units.MetersToUnits;

		var root = scene.CreateObject();
		root.Name = def.Name;
		root.Tags.Add( "car" );
		root.WorldPosition = position;
		root.WorldRotation = rotation;

		var rigidbody = root.Components.Create<Rigidbody>();
		rigidbody.MassOverride = def.Mass;
		rigidbody.AngularDamping = 0.3f; // mild yaw settling on all cars
		rigidbody.OverrideMassCenter = true;
		rigidbody.MassCenterOverride = Vector3.Down * def.CenterOfMassDrop * m; // low CoM: better roll behavior

		// collider must stay ABOVE the wheels' contact zone or the car rests on its belly
		// and the suspension traces never reach the ground: bottom sits at ride clearance
		float colliderHeight = MathF.Max( def.BodySize.z * 0.55f, 0.2f );
		float colliderBottom = -(def.RideHeight - def.GroundClearance);
		var collider = root.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( def.BodySize.x, def.BodySize.y, colliderHeight ) * m;
		collider.Center = Vector3.Up * (colliderBottom + colliderHeight * 0.5f) * m;

		// Custom body seam: a consumer can plug in a body builder. Null (default) or a false return
		// falls back to the primitive blockout path so a failed custom build never bricks a spawn.
		// Physics above is identical on every path (same rigidbody, root collider, mass/CoM overrides).
		bool customBody = CustomBodyBuilder is not null && CustomBodyBuilder( scene, root, def );

		if ( !customBody )
		{
			if ( def.Style == BodyStyle.Kart )
				BuildKartBody( scene, root, def ); // adds its own driver when HasDriver
			else
				BuildBoxBody( scene, root, def );
		}
		else if ( def.HasDriver )
		{
			// A custom body that wants the shared engine citizen driver gets the default seat pose;
			// a builder that seats its own driver can leave HasDriver false.
			AddDriver( scene, root, def, DefaultDriverLocalM );
		}

		var controller = root.Components.Create<VehicleController>();
		controller.Definition = def;

		// Shared placeholder engine audio on every car: one positional looping tone pitched by live RPM
		// and swelled by throttle. Reads the drivetrain off the controller; tuned live via the
		// vp_engine_sound / vp_engine_volume console dials.
		root.Components.Create<EngineAudio>();

		// Skid / traction-loss audio: a positional loop driven by the worst wheel slip (handbrake,
		// power-slide, or lockup). Reads the controller's wheels; muted below a slip threshold.
		root.Components.Create<SkidAudio>();

		// Static wheel load from the LIVE scene gravity: the demo scene runs ~1.1 g explicitly, so
		// mass·9.81/4 would under-reference the true static load and bias the load-sensitivity curve.
		// Read gravity the same way SeatHeightM does.
		float gravity = scene.PhysicsWorld is { } pw
			? pw.Gravity.Length * Units.UnitsToMeters
			: 9.81f;
		float staticLoad = def.Mass * gravity / 4f;

		// wheels: FL, FR, RL, RR at (+-wheelbase/2, +-track/2, -rideHeight)
		for ( int i = 0; i < 4; i++ )
		{
			bool front = i < 2;
			bool left = i % 2 == 0;

			var attach = new Vector3(
				(front ? 1f : -1f) * def.Wheelbase * 0.5f,
				(left ? 1f : -1f) * def.TrackWidth * 0.5f,
				-def.RideHeight ) * m;

			var wheelGo = scene.CreateObject();
			wheelGo.Name = front ? (left ? "Wheel FL" : "Wheel FR") : (left ? "Wheel RL" : "Wheel RR");
			wheelGo.SetParent( root, false );
			wheelGo.LocalPosition = attach;

			var wheel = wheelGo.Components.Create<VehicleWheel>();
			wheel.Radius = def.WheelRadius;
			wheel.Inertia = def.WheelInertia;
			wheel.SuspensionTravel = def.SuspensionTravel;
			wheel.SpringRate = def.SpringRate;
			wheel.DamperRate = def.DamperRate;
			wheel.LongitudinalCurve = def.LongitudinalCurve;
			wheel.LateralCurve = def.LateralCurve;
			wheel.LoadSensitivity = def.LoadSensitivity;
			wheel.StaticLoad = staticLoad;
			wheel.IsSteering = front;
			wheel.HasHandbrake = !front;
			wheel.IsDriven = def.Layout switch
			{
				DriveLayout.FWD => front,
				DriveLayout.RWD => !front,
				_ => true
			};

			controller.Wheels.Add( wheel );

			BuildBlockoutWheelVisual( scene, wheelGo, wheel, def );
		}

		return root;
	}

	/// <summary>Suspension-equilibrium spawn height above the ground (SI m) so the car settles level
	/// instead of dropping or flinging on spawn. Springs already carry the weight at rest, so this is
	/// NOT surface+radius. Reads live scene gravity. Callers add this to their ground spawn point.</summary>
	public static float SeatHeightM( CarDefinition def )
	{
		float gravity = Game.ActiveScene?.PhysicsWorld is { } pw
			? pw.Gravity.Length * Units.UnitsToMeters
			: 9.81f;
		float staticCompression = def.Mass * gravity / 4f / def.SpringRate;
		return def.SuspensionTravel + def.WheelRadius - staticCompression + def.RideHeight;
	}

	/// <summary>Build the VISIBLE blockout wheel (squashed dev sphere) under <paramref name="wheelGo"/>.
	/// The shared wheel visual for the blockout body path; a custom body builder that wants modeled
	/// wheels can replace these under each wheel GameObject after spawn.</summary>
	public static void BuildBlockoutWheelVisual( Scene scene, GameObject wheelGo, VehicleWheel wheel, CarDefinition def )
	{
		float m = Units.MetersToUnits;

		var visualGo = scene.CreateObject();
		visualGo.Name = "Visual (blockout)";
		visualGo.SetParent( wheelGo, false );

		// blockout: squashed sphere, drawn chunkier than physics radius
		bool kart = def.Style == BodyStyle.Kart;
		float diameterScale = def.WheelRadius * m / 50f * (kart ? 1.3f : 1.1f);
		visualGo.LocalScale = new Vector3( diameterScale, diameterScale * (kart ? 0.85f : 0.6f), diameterScale );

		var sphereRenderer = visualGo.Components.Create<ModelRenderer>();
		sphereRenderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		sphereRenderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		sphereRenderer.Tint = new Color( 0.12f, 0.12f, 0.13f );

		var visual = visualGo.Components.Create<WheelVisual>();
		visual.Wheel = wheel;
	}

	static void BuildBoxBody( Scene scene, GameObject root, CarDefinition def )
	{
		AddBox( scene, root, "Body", Vector3.Zero, def.BodySize, def.Tint );
		AddBox( scene, root, "Cabin",
			new Vector3( -def.BodySize.x * 0.08f, 0f, def.BodySize.z * 0.72f ),
			new Vector3( def.BodySize.x * 0.45f, def.BodySize.y * 0.85f, def.BodySize.z * 0.6f ),
			def.Tint.Darken( 0.35f ) );
	}

	static void BuildKartBody( Scene scene, GameObject root, CarDefinition def )
	{
		var frame = def.Tint.Darken( 0.35f );
		var dark = new Color( 0.15f, 0.15f, 0.16f );
		var chrome = new Color( 0.75f, 0.76f, 0.78f );

		// chassis
		AddBox( scene, root, "Deck", new Vector3( 0f, 0f, 0f ), new Vector3( 1.5f, 0.9f, 0.10f ), def.Tint );
		AddBox( scene, root, "Nose", new Vector3( 0.88f, 0f, 0.04f ), new Vector3( 0.5f, 0.5f, 0.14f ), def.Tint );
		AddBox( scene, root, "NoseWing", new Vector3( 1.05f, 0f, 0.02f ), new Vector3( 0.16f, 0.95f, 0.06f ), frame );
		AddBox( scene, root, "PodL", new Vector3( 0.05f, 0.52f, 0.06f ), new Vector3( 0.9f, 0.16f, 0.14f ), frame );
		AddBox( scene, root, "PodR", new Vector3( 0.05f, -0.52f, 0.06f ), new Vector3( 0.9f, 0.16f, 0.14f ), frame );
		AddBox( scene, root, "RearBumper", new Vector3( -0.78f, 0f, 0.10f ), new Vector3( 0.10f, 0.95f, 0.16f ), frame );

		// seat
		AddBox( scene, root, "SeatBase", new Vector3( -0.42f, 0f, 0.10f ), new Vector3( 0.42f, 0.44f, 0.10f ), dark );
		AddBox( scene, root, "SeatBack", new Vector3( -0.60f, 0f, 0.32f ), new Vector3( 0.10f, 0.44f, 0.46f ), dark );

		// roll bar behind the seat
		AddBox( scene, root, "RollPostL", new Vector3( -0.68f, 0.18f, 0.38f ), new Vector3( 0.06f, 0.06f, 0.62f ), chrome );
		AddBox( scene, root, "RollPostR", new Vector3( -0.68f, -0.18f, 0.38f ), new Vector3( 0.06f, 0.06f, 0.62f ), chrome );
		AddBox( scene, root, "RollTop", new Vector3( -0.68f, 0f, 0.70f ), new Vector3( 0.06f, 0.44f, 0.06f ), chrome );

		// controls + engine
		AddBox( scene, root, "Column", new Vector3( 0.42f, 0f, 0.20f ), new Vector3( 0.05f, 0.05f, 0.34f ), chrome );
		AddBox( scene, root, "SteeringWheel", new Vector3( 0.30f, 0f, 0.40f ), new Vector3( 0.05f, 0.30f, 0.20f ), dark );
		AddBox( scene, root, "EngineBlock", new Vector3( -0.52f, -0.34f, 0.14f ), new Vector3( 0.30f, 0.22f, 0.20f ), dark );
		AddBox( scene, root, "Exhaust", new Vector3( -0.80f, -0.34f, 0.12f ), new Vector3( 0.28f, 0.08f, 0.08f ), chrome );

		if ( def.HasDriver )
			AddDriver( scene, root, def );
	}

	static void AddDriver( Scene scene, GameObject root, CarDefinition def )
		=> AddDriver( scene, root, def, DefaultDriverLocalM );

	static void AddDriver( Scene scene, GameObject root, CarDefinition def, Vector3 localMeters )
	{
		float m = Units.MetersToUnits;

		var driverGo = scene.CreateObject();
		driverGo.Name = "Driver";
		driverGo.SetParent( root, false );
		// citizen root is at the feet; seated pose sits on an invisible chair behind the origin.
		driverGo.LocalPosition = localMeters * m;

		var renderer = driverGo.Components.Create<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );

		// dress the citizen — a simple default outfit via the engine clothing system. Each item is
		// null-checked so a missing asset degrades to a bare driver rather than bricking the spawn.
		DressDriver( renderer );

		// citizen animgraph: sit is an enum (0 none, 1-3 chair poses, 4-5 ground). Per-car (def) so a
		// recumbent kart pose (legs forward to the pedals) doesn't disturb any upright-seated car.
		renderer.Set( "b_grounded", true );
		renderer.Set( "sit", def.DriverSit );
		renderer.Set( "sit_offset_height", def.DriverSitOffsetHeight );
	}

	/// <summary>A plain default outfit for the seated citizen driver: a single-piece jumpsuit + shoes.
	/// Loaded from the shipped citizen_clothes resources via the engine clothing system; any item that
	/// fails to resolve is skipped so a bare driver is the worst case, never a broken spawn.</summary>
	static readonly string[] DriverOutfit =
	{
		"models/citizen_clothes/shirt/Jumpsuit/blue_jumpsuit.clothing",
		"models/citizen_clothes/shoes/Trainers/trainers.clothing",
	};

	static void DressDriver( SkinnedModelRenderer renderer )
	{
		var outfit = new ClothingContainer();
		bool any = false;
		foreach ( var path in DriverOutfit )
		{
			var item = ResourceLibrary.Get<Clothing>( path );
			if ( item is null )
			{
				Log.Warning( $"[vp] driver clothing '{path}' did not resolve — skipping (bare on that slot)" );
				continue;
			}
			outfit.Add( item );
			any = true;
		}
		if ( any )
			outfit.Apply( renderer );
	}

	static void AddBox( Scene scene, GameObject parent, string name, Vector3 positionMeters, Vector3 sizeMeters, Color color )
	{
		float m = Units.MetersToUnits;

		var go = scene.CreateObject();
		go.Name = name;
		go.SetParent( parent, false );
		go.LocalPosition = positionMeters * m;
		go.LocalScale = sizeMeters * m / 50f;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = color;
	}
}
