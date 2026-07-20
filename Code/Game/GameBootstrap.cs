namespace VehicleProto;

/// <summary>
/// Scene entry point: builds the proving-grounds city, spawns the default car at the
/// spawn point, wires camera + HUD + tuning panel. Controls: WASD drive, Space handbrake,
/// R respawn, T tuning panel.
/// </summary>
public sealed class GameBootstrap : Component
{
	/// <summary>Feature gate for in-game world switching (the World &amp; Terrain panel + its M hotkey).
	/// OFF again (stunt merge 2026-07-19): the stunt park now lives IN the main world as drive-in
	/// zones on the hardpack (<see cref="PlaygroundBuilder.BuildProtoStuntZones"/>), so players never
	/// need a world switch - they drive east through the gate and into it. The separate playground
	/// world is dev-only via the console (<c>vp_world</c>, <c>vp_setworld</c>); the M panel is retired.
	/// (static readonly, not const, so the gated call sites don't constant-fold into dead-code warnings.)</summary>
	public static readonly bool WorldSwitchEnabled = false;

	/// <summary>World selector (world-pass 2026-07-13). "proto" (DEFAULT) = the measurement scene:
	/// CityBuilder + TestTrack with all 11 stations — the battery + vp_test.py depend on this, so it
	/// MUST stay the default (vp_test never sets the ConVar). "playground" = the human-facing
	/// drive space (<see cref="PlaygroundBuilder"/>): prefab buildings, wider roads, jump field, banked
	/// bowl, loop track. Set it in-console before Play: <c>vp_world playground</c>.</summary>
	[ConVar( "vp_world" )]
	public static string World { get; set; } = "proto";

	/// <summary>W4 terrain toggle for the playground world (world-pass §4 Option B). Default OFF =
	/// flat plane (Option A, the safe raycast-wheel baseline). <c>vp_terrain 1</c>
	/// before Play swaps in the gentle heightfield. Ignored unless vp_world=playground.</summary>
	[ConVar( "vp_terrain" )]
	public static bool Terrain { get; set; } = false;

	/// <summary>PERF-BASELINE arming flag (Phase 0, measurement-only). Default OFF = zero behavior
	/// change: the world build + car spawn run exactly as normal. When set BEFORE Play (either
	/// <c>vp_perf_boot 1</c> in-console, or armed by the editor <c>vp_perf</c> tool's static write
	/// before <c>play_start</c>), the one-shot build/spawn timings are wrapped in a Stopwatch and
	/// logged as greppable <c>[vp] PERF BUILD/KIT</c> lines. It only measures — it never changes what
	/// is built.</summary>
	[ConVar( "vp_perf_boot" )]
	public static bool PerfBoot { get; set; } = false;

	/// <summary>Root GameObject names the world builders create (CityBuilder="City",
	/// TestTrack="Proving Grounds", Outskirts="Outskirts", PlaygroundBuilder="Playground" for the
	/// dev-only world and "Stunt Zones" for the proto-hosted stunt content). A live
	/// world switch tears these down by name before rebuilding — the builders are static and simply
	/// create fresh roots.</summary>
	static readonly string[] WorldRootNames = { "City", "Proving Grounds", "Outskirts", "Playground", "Stunt Zones" };

	// Kept so a live world switch (WorldControls panel) can rewire in place instead of remounting.
	VehiclePilot _pilot;

	// Session-reset the harness bridge BEFORE the pilot reads any command, so stale statics from
	// a previous Play session can't make a fresh one no-op (the Play→Stop→Play stale-static trap).
	protected override void OnEnabled()
	{
		VehicleBridge.ResetSession();
	}

	protected override void OnStart()
	{
		float m = Units.MetersToUnits;

		// belt-and-braces: OnEnabled already reset, but guarantee a clean bridge before spawns.
		VehicleBridge.ResetSession();

		// Apply the player's saved master audio volume before anything can play a sound, so the
		// persisted level takes effect on startup (not only when the Session menu is opened).
		UserSettings.ApplyMasterVolume();

		// s&box default gravity is Source-style ~2.2 g which bottoms out real spring
		// rates; ~1.1 g keeps the suspension math honest (lesson from anywheredrive)
		Scene.PhysicsWorld.Gravity = Vector3.Down * 9.81f * 1.1f * m;

		// Wire the Vehicle Physics Kit seams to the game before the first spawn or camera use: the
		// part-kit body builder (CustomBodyBuilder) and the camera cursor-modal check (CursorModalOpen).
		PartKitFactory.InstallSeams();

		// Build the selected world + spawn the default hatch (the shared path a live world switch reuses).
		var controller = BuildWorldAndCar( CarDefinitions.Hatch );

		var hudGo = Scene.CreateObject();
		hudGo.Name = "HUD";
		UiRig.Mount( hudGo, controller );

		// play-mode test director: consumes vp_drive/vp_spawn bridge commands and
		// drives scripted maneuvers through the controller's InputOverride seam. Seed it with the
		// city car so the ConVar/first maneuver has a car even before a vp_spawn relocates it.
		_pilot = Components.Create<VehiclePilot>();
		_pilot.ActiveCar = controller;

		// Phase-0 perf probe: created ONCE, fully inert until armed via the vp_perf channel (it applies
		// no forces and samples nothing until a capture is requested). Its own GameObject so it never
		// perturbs the pilot/HUD stacks.
		var perfGo = Scene.CreateObject();
		perfGo.Name = "PerfProbe";
		perfGo.Components.Create<PerfProbe>();

		Log.Info( $"[vp] boot started. v{VpBuild.Version} build {VpBuild.PublishStamp} ({VpBuild.PublishStampNote}). WASD drive, Space handbrake, R respawn, T tuning, I help, H hide HUD. Pilot ready." );
	}

	/// <summary>Build the selected world and spawn <paramref name="def"/> at its spawn point, wiring
	/// the chase camera. Default "proto" = the measurement scene (battery-anchored; vp_test.py leaves
	/// vp_world at the default); "playground" = the human-facing drive space. Records the world
	/// actually built into <see cref="VehicleBridge.World"/> so the runner's fail-closed gate reads the
	/// TRUE world (audit 2026-07-13 HIGH), not just a persisted ConVar. Returns the new controller.</summary>
	VehicleController BuildWorldAndCar( CarDefinition def )
	{
		float m = Units.MetersToUnits;

		bool playground = string.Equals( World, "playground", System.StringComparison.OrdinalIgnoreCase );
		VehicleBridge.World = playground ? "playground" : "proto";

		// Phase-0 (measurement-only): time the world build when armed. Inert by default (PerfBoot off).
		var buildSw = PerfBoot ? System.Diagnostics.Stopwatch.StartNew() : null;

		Vector3 spawnPosM;
		Rotation spawnFacing;
		if ( playground )
		{
			var pg = PlaygroundBuilder.Build( Scene, Terrain );
			spawnPosM = pg.SpawnPosition;
			spawnFacing = pg.SpawnFacing;
		}
		else
		{
			var city = CityBuilder.Build( Scene );
			// proving-grounds test track, ~600 m east of the city (docs/proving-grounds.md)
			TestTrack.Build( Scene, new Vector3( 600f, 0f, 0f ) );
			// outskirts belt (world pass 2026-07-19): ring road + city gates + the connector that
			// makes the proving grounds drivable-to; seals the combined world with its own perimeter
			Outskirts.Build( Scene );
			// stunt zones ON the hardpack (stunt merge 2026-07-19): drive-in stunt content in three
			// station-clear zone rectangles; the separate playground world stays dev-only
			PlaygroundBuilder.BuildProtoStuntZones( Scene );
			spawnPosM = city.SpawnPosition;
			spawnFacing = city.SpawnFacing;
		}

		if ( buildSw is not null )
			Log.Info( $"[vp] PERF BUILD world={VehicleBridge.World} ms={buildSw.Elapsed.TotalMilliseconds:0.0}" );

		// spawn at exact static rest height: springs already carrying the weight. Spawning at
		// surface+radius instead of suspension equilibrium causes a violent launch — all four springs
		// hit full compression at once (known gotcha). SeatHeightM is the ONE canonical formula.
		float seatZM = VehiclePilot.SeatHeightM( def );
		// Both the world spawn point (spawnPosM, authored in METRES like all layout geometry) and the
		// seat-height offset are in metres; VehicleFactory.Spawn wants engine units, so convert the whole
		// position at this one boundary. Proto's spawn is (0,0,0) so the conversion is a no-op for it —
		// only the playground's -150 m west apron actually moves (previously it landed at unit-space -150).
		var car = PartKitFactory.Spawn( Scene, def,
			(spawnPosM + Vector3.Up * seatZM) * m,
			spawnFacing );
		var controller = car.Components.Get<VehicleController>();
		VehicleBridge.SpawnedCar = CarSwitcher.CurrentId( controller );

		var camGo = Scene.Camera?.GameObject;
		if ( camGo is not null )
		{
			var chase = camGo.Components.GetOrCreate<VehicleCamera>();
			chase.Target = controller;
			chase.Distance = def.BodySize.x * 1.2f + 2f;
			chase.Height = def.BodySize.z + 1.3f;
		}

		return controller;
	}

	/// <summary>Live world/terrain switch from the WorldControls panel (M). This is the REAL apply
	/// path the public UX needs — not a silent ConVar set. It tears down the current world roots + the
	/// current car, rebuilds the newly-selected world, and rewires the chase camera, the pilot, and the
	/// whole HUD stack in place (the same live-surgery pattern <see cref="CarSwitcher"/> uses for cars,
	/// one level up). The player's CURRENT car is preserved across the switch. <see cref="VehicleBridge.World"/>
	/// is updated so the measurement-world gate in vp_test.py stays honest: after switching to playground
	/// the runner refuses; after switching back to proto it passes.</summary>
	public void ApplyWorld( string world, bool terrain )
	{
		World = string.Equals( world, "playground", System.StringComparison.OrdinalIgnoreCase ) ? "playground" : "proto";
		Terrain = terrain;

		// preserve the player's current car so a world switch doesn't silently reset it to the hatch.
		var def = _pilot?.ActiveCar.IsValid() == true ? _pilot.ActiveCar.Definition : CarDefinitions.Hatch;

		// tear down the current car FIRST — it sits above the ground we're about to delete.
		if ( _pilot?.ActiveCar.IsValid() == true )
			_pilot.ActiveCar.GameObject.Destroy();

		// tear down the old world roots by name (collect first — don't mutate mid-enumeration).
		var stale = Scene.GetAllObjects( true ).Where( go => go is not null && WorldRootNames.Contains( go.Name ) ).ToList();
		foreach ( var go in stale )
			go.Destroy();

		var controller = BuildWorldAndCar( def );

		if ( _pilot is not null )
			_pilot.ActiveCar = controller;
		UiRig.Retarget( Scene, controller );

		Log.Info( $"[vp] world switch -> {VehicleBridge.World} terrain={Terrain}" );
	}

	/// <summary>Panel/console entry: find the live bootstrap and apply a world switch. Safe to call
	/// with no active bootstrap (returns quietly).</summary>
	public static void RequestWorld( Scene scene, string world, bool terrain )
	{
		var boot = scene?.GetAllComponents<GameBootstrap>().FirstOrDefault();
		boot?.ApplyWorld( world, terrain );
	}

	/// <summary>Console parity for the M panel's world switch (handy for testing / headless UI
	/// verification, where the M keypress can't be injected): <c>vp_setworld playground 1</c>. This
	/// drives the IDENTICAL <see cref="RequestWorld"/> path the panel button's onclick calls.</summary>
	[ConCmd( "vp_setworld" )]
	public static void VpSetWorld( string world = "proto", bool terrain = false )
	{
		RequestWorld( Game.ActiveScene, world, terrain );
	}

}
