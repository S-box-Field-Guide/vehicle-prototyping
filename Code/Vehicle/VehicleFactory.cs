namespace VehicleProto;

/// <summary>
/// Builds a drivable blockout car from a CarDefinition at runtime: box body, sphere wheels
/// (spec 5.3 - 30-minute blockouts genuinely fit the art style). No prefab assets needed;
/// swap to real models later without touching physics.
/// </summary>
public static class VehicleFactory
{
	public static GameObject Spawn( Scene scene, CarDefinition def, Vector3 position, Rotation rotation )
	{
		float m = Units.MetersToUnits;

		// Phase-0 (measurement-only): time the full kit-assembly spawn when armed (GameBootstrap.PerfBoot).
		// Inert by default; covers every spawn path (initial, vp_spawn, car switch) so each logs one line.
		var spawnSw = GameBootstrap.PerfBoot ? System.Diagnostics.Stopwatch.StartNew() : null;

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

		// Part-kit path (Stage A): body assembled from separate part GameObjects; wheel
		// meshes mount per-wheel below. Falls back to the primitive blockout path on any kit
		// failure so a broken manifest never bricks a spawn. Physics above is NEAR-identical
		// on every path: same rigidbody, root collider, raycast wheels, and mass/CoM are
		// pinned by the overrides — but the kit path's per-part compound colliders shift the
		// derived INERTIA TENSOR (not overridden): a small real delta (≤1.6% hatch slalom
		// A/B) that only matters at a maneuver's stability limit. Root cause: compound
		// child colliders shift the derived inertia tensor, not the mass.
		PartKitManifest kit = null;
		Dictionary<string, Model> wheelModels = null;
		bool kitBody = !string.IsNullOrEmpty( def.PartKitManifest )
			&& PartKitAssembler.TryBuildBody( scene, root, def, out kit, out wheelModels );

		// Honest failure (2026-07-15): a car whose part kit did NOT assemble falls
		// back to a primitive engine blockout — box/kart body + sphere wheels — NOT a fused stand-in
		// model, and says so loudly. A broken kit must never null-ref or load a removed asset; the
		// blockout keeps the car drivable so the failure is visible, not fatal.
		if ( !kitBody )
		{
			if ( !string.IsNullOrEmpty( def.PartKitManifest ) )
				Log.Error( $"[vp] part kit '{def.PartKitManifest}' failed to assemble for '{def.Name}' "
					+ "— spawning primitive blockout (no fused fallback)" );

			if ( def.Style == BodyStyle.Kart )
				BuildKartBody( scene, root, def );
			else
				BuildBoxBody( scene, root, def );
		}

		// Kit-path driver (de-Kenney kart, directive 2026-07-13): ALWAYS the engine
		// citizen through the same AddDriver seam the blockout path uses — never a
		// modeled figure. The manifest may carry the sit point (driver_seat_author_m);
		// DriverLocalM converts it to the root frame via the live seat height.
		if ( kitBody && def.HasDriver )
			AddDriver( scene, root, def, kit.DriverLocalM( VehiclePilot.SeatHeightM( def ) ) );

		var controller = root.Components.Create<VehicleController>();
		controller.Definition = def;

		// Shared placeholder engine audio on every car path (kit, blockout): one positional
		// looping tone pitched by live RPM and swelled by throttle. Reads the drivetrain off the controller
		// created above; tuned live via the vp_engine_sound / vp_engine_volume console dials.
		root.Components.Create<EngineAudio>();

		// Skid / traction-loss audio: a positional loop driven by the worst wheel slip (handbrake, power-
		// slide, or lockup). Reads the controller's wheels; muted below a slip threshold; vp_skid_volume dial.
		root.Components.Create<SkidAudio>();

		// Static wheel load from the LIVE scene gravity (audit 2026-07-12 #3): the scene runs
		// ~1.1 g explicitly, so mass·9.81/4 under-referenced the true static load by ~10% and
		// biased the tire load-sensitivity curve. Read gravity the same way SeatHeightM does.
		float gravity = scene.PhysicsWorld is { } pw
			? pw.Gravity.Length * Units.UnitsToMeters
			: 9.81f;
		float staticLoad = def.Mass * gravity / 4f;

		// wheels: FL, FR, RL, RR at (+-wheelbase/2, +-track/2, -rideHeight)
		int wheelOffenders = 0; // kit wheels that fell back to a blockout visual (target 0)
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

			if ( kitBody )
			{
				// wheel models were preloaded in the SAME transaction as the body (audit 2026-07-13 HIGH),
				// so a missing wheel .vmdl already aborted the kit pre-commit → we never reach here with one.
				// MountWheelVisual returns false only on the belt-and-braces post-commit failure, where it
				// drops a VISIBLE blockout wheel instead of an invisible one; count it as an audit offender.
				if ( !PartKitAssembler.MountWheelVisual( scene, wheelGo, wheel, kit, def, front, left, wheelModels ) )
					wheelOffenders++;
			}
			else
				BuildBlockoutWheelVisual( scene, wheelGo, wheel, def );
		}

		if ( kitBody )
			Log.Info( $"[vp] AUDIT partkit_wheels offenders={wheelOffenders} target 0" );

		// Spawn-identity invariant (target 0): the manifest actually assembled must be the one the
		// definition asked for — the loaded kit's own name must match the kit folder named in
		// def.PartKitManifest. A mismatch means a body was built for a DIFFERENT car than its
		// definition/HUD identity (the class of defect where the on-screen readout and the body
		// disagree). It should be impossible, so surface it loudly as an offender rather than
		// shipping a silent lie.
		if ( kitBody )
		{
			string wantKit = KitIdFromPath( def.PartKitManifest );
			bool idMatch = string.Equals( wantKit, kit.kit, System.StringComparison.OrdinalIgnoreCase );
			Log.Info( $"[vp] AUDIT spawn_identity offenders={( idMatch ? 0 : 1 )} target 0 "
				+ $"(def='{def.Name}' wants='{wantKit}' built='{kit.kit}')" );
		}

		if ( spawnSw is not null )
			Log.Info( $"[vp] PERF KIT car={def.Name} kit={( kitBody ? 1 : 0 )} ms={spawnSw.Elapsed.TotalMilliseconds:0.0}" );

		return root;
	}

	/// <summary>The kit id a manifest path names — the folder under models/vehicles/
	/// ("models/vehicles/hatch_kit/manifest.json" -> "hatch_kit"). Used by the spawn-identity audit
	/// to confirm the assembled manifest is the one the definition requested.</summary>
	static string KitIdFromPath( string manifestPath )
	{
		if ( string.IsNullOrEmpty( manifestPath ) )
			return "";
		var parts = manifestPath.Replace( '\\', '/' ).Split( '/' );
		// .../<kit_id>/manifest.json → the segment before the file name
		return parts.Length >= 2 ? parts[^2] : "";
	}

	/// <summary>Build the VISIBLE blockout wheel (squashed dev sphere) under <paramref name="wheelGo"/>.
	/// The shared fallback for both the blockout body path (a kit that failed to assemble) and the
	/// part-kit belt-and-braces path (audit 2026-07-13 HIGH): a kit wheel whose model fails to load
	/// POST-commit gets this rather than an invisible wheel — a missable "one invisible wheel" defect
	/// becomes an obvious grey blockout + an audit offender.</summary>
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
		=> AddDriver( scene, root, def, PartKitManifest.DefaultDriverLocalM );

	static void AddDriver( Scene scene, GameObject root, CarDefinition def, Vector3 localMeters )
	{
		float m = Units.MetersToUnits;

		var driverGo = scene.CreateObject();
		driverGo.Name = "Driver";
		driverGo.SetParent( root, false );
		// citizen root is at the feet; seated pose sits on an invisible chair behind the origin.
		// Kit vehicles may pin the sit point via manifest driver_seat_author_m (kart bucket seat).
		driverGo.LocalPosition = localMeters * m;

		var renderer = driverGo.Components.Create<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );

		// dress the citizen — it spawned bare-body before (feel feedback 2026-07-13). A simple
		// default outfit via the engine clothing system. Whitelist-safe (ResourceLibrary.Get<Clothing>
		// + ClothingContainer.Apply); each item is null-checked so a missing asset degrades to a bare
		// driver rather than bricking the spawn.
		DressDriver( renderer );

		// citizen animgraph: sit is an enum (0 none, 1-3 chair poses, 4-5 ground). Per-car (def) so a
		// recumbent kart pose (legs forward to the pedals) doesn't disturb any upright-seated car.
		renderer.Set( "b_grounded", true );
		renderer.Set( "sit", def.DriverSit );
		renderer.Set( "sit_offset_height", def.DriverSitOffsetHeight );
	}

	/// <summary>A plain default outfit for the seated citizen driver: a single-piece jumpsuit + shoes
	/// (jumpsuit reads as a driver's suit and covers torso+legs in one item). Loaded from the shipped
	/// citizen_clothes resources via the engine clothing system; any item that fails to resolve is
	/// skipped so a bare driver is the worst case, never a broken spawn.</summary>
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
