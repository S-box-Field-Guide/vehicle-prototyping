namespace VehicleProto;

/// <summary>
/// Game-side spawn front end that consumes the Vehicle Physics Kit's <see cref="VehicleFactory"/>
/// and re-adds the part-kit body program that lived in the game's old factory. The kit builds the
/// physics (rigidbody, root collider, raycast wheels, controller, audio) and a primitive blockout
/// body/wheels; the two things the kit's generic factory does NOT do are wired back in here:
///
///  1. Body: the <see cref="VehicleFactory.CustomBodyBuilder"/> seam runs the existing
///     <see cref="PartKitAssembler.TryBuildBody"/> path (part GameObjects + their manifest-sized
///     BoxColliders, which are what shift the derived inertia tensor — so this is a PHYSICS-relevant
///     reproduction, not cosmetic). The seam preserves the old decision logic exactly: a car with a
///     body manifest takes the part-kit path, else the kit builds the blockout body.
///  2. Post-spawn visual finish (cosmetic, no colliders, zero telemetry impact): the kit's blockout
///     wheel visuals are swapped for the modeled kit wheels (<see cref="PartKitAssembler.MountWheelVisual"/>),
///     the kart citizen driver is moved from the kit's default seat to the manifest sit point, and the
///     part-kit wheel + spawn-identity audits and the PERF-KIT timing line are emitted, byte-for-byte
///     as the old factory logged them.
///
/// Seat height uses the game's <see cref="VehiclePilot.SeatHeightM"/> everywhere the old factory did,
/// so the part-kit body position (and thus its compound colliders / inertia tensor) is unchanged.
/// </summary>
public static class PartKitFactory
{
	// Stash filled by the CustomBodyBuilder seam during VehicleFactory.Spawn and read back by Spawn()
	// immediately after it returns. Spawns are synchronous and single-threaded, so a single slot is safe.
	static PartKitManifest _pendingKit;
	static Dictionary<string, Model> _pendingWheelModels;
	static bool _pendingKitBody;

	/// <summary>Wire the kit's static seams to the game. Idempotent (the delegates target static
	/// methods, so re-assigning across Play sessions is harmless). Called from GameBootstrap and,
	/// defensively, from <see cref="Spawn"/>.</summary>
	public static void InstallSeams()
	{
		// Body seam: run the game's part-kit assembler for cars that carry a body manifest, exactly as
		// the old factory decided; else return false so the kit builds its primitive blockout body.
		VehicleFactory.CustomBodyBuilder = BuildPartKitBody;

		// Camera cursor seam: the kit hides the cursor for drive input unless a game UI modal owns it.
		VehicleCamera.CursorModalOpen = () => UiState.AnyCursorModalOpen;
	}

	/// <summary>Spawn a drivable car through the kit factory, then re-add the part-kit visual finish
	/// and audits. Drop-in replacement for the old <c>VehicleFactory.Spawn</c> game call.</summary>
	public static GameObject Spawn( Scene scene, CarDefinition def, Vector3 position, Rotation rotation )
	{
		InstallSeams();

		// Phase-0 (measurement-only): time the full spawn when armed (GameBootstrap.PerfBoot). Inert by
		// default; covers every spawn path (initial, vp_spawn, car switch) so each logs one line.
		var spawnSw = GameBootstrap.PerfBoot ? System.Diagnostics.Stopwatch.StartNew() : null;

		_pendingKit = null;
		_pendingWheelModels = null;
		_pendingKitBody = false;

		var root = VehicleFactory.Spawn( scene, def, position, rotation );

		bool kitBody = _pendingKitBody;
		var kit = _pendingKit;
		var wheelModels = _pendingWheelModels;

		if ( kitBody && kit is not null )
		{
			float m = Units.MetersToUnits;

			// Kit-path driver (de-Kenney kart, directive 2026-07-13): the kit already built the shared
			// engine citizen at its DEFAULT seat when HasDriver; move it to the manifest sit point, the
			// same DriverLocalM the old factory seated it at. Manifest with no sit point -> unchanged.
			if ( def.HasDriver )
			{
				var driver = root.Children.FirstOrDefault( c => c.Name == "Driver" );
				if ( driver is not null )
					driver.LocalPosition = kit.DriverLocalM( VehiclePilot.SeatHeightM( def ) ) * m;
			}

			// Swap the kit's primitive blockout wheel visuals for the modeled kit wheels. Physics wheels
			// (VehicleWheel) are untouched; only the visual under each wheel GO changes. A wheel model that
			// fails to mount post-commit drops a VISIBLE blockout wheel + counts an audit offender.
			int wheelOffenders = 0;
			var wheels = root.Components.Get<VehicleController>()?.Wheels;
			if ( wheels is not null )
			{
				for ( int i = 0; i < wheels.Count; i++ )
				{
					var wheel = wheels[i];
					bool front = i < 2;
					bool left = i % 2 == 0;
					var wheelGo = wheel.GameObject;

					// remove the kit's blockout visual before mounting the modeled wheel
					var blockout = wheelGo.Children.FirstOrDefault( c => c.Name == "Visual (blockout)" );
					blockout?.Destroy();

					if ( !PartKitAssembler.MountWheelVisual( scene, wheelGo, wheel, kit, def, front, left, wheelModels ) )
						wheelOffenders++;
				}
			}

			Log.Info( $"[vp] AUDIT partkit_wheels offenders={wheelOffenders} target 0" );

			// Spawn-identity invariant (target 0): the manifest actually assembled must be the one the
			// definition asked for — the loaded kit's own name must match the kit folder named in
			// def.BodyManifest. A mismatch means a body was built for a DIFFERENT car than its
			// definition/HUD identity; surface it loudly as an offender rather than shipping a silent lie.
			string wantKit = KitIdFromPath( def.BodyManifest );
			bool idMatch = string.Equals( wantKit, kit.kit, System.StringComparison.OrdinalIgnoreCase );
			Log.Info( $"[vp] AUDIT spawn_identity offenders={( idMatch ? 0 : 1 )} target 0 "
				+ $"(def='{def.Name}' wants='{wantKit}' built='{kit.kit}')" );
		}

		if ( spawnSw is not null )
			Log.Info( $"[vp] PERF KIT car={def.Name} kit={( kitBody ? 1 : 0 )} ms={spawnSw.Elapsed.TotalMilliseconds:0.0}" );

		return root;
	}

	/// <summary>CustomBodyBuilder seam body: run the part-kit assembler for a car that carries a body
	/// manifest. Returns true if a part-kit body was assembled (the kit then skips its blockout body and
	/// this class finishes wheels/driver post-spawn); false keeps the kit's primitive blockout path,
	/// exactly as the old factory fell back. Stashes the loaded kit + wheel models for the post-pass.</summary>
	static bool BuildPartKitBody( Scene scene, GameObject root, CarDefinition def )
	{
		_pendingKit = null;
		_pendingWheelModels = null;
		_pendingKitBody = false;

		if ( string.IsNullOrEmpty( def.BodyManifest ) )
			return false;

		if ( !PartKitAssembler.TryBuildBody( scene, root, def, out var kit, out var wheelModels ) )
		{
			// Honest failure (2026-07-15): a car whose part kit did NOT assemble falls back to a
			// primitive engine blockout — box/kart body + sphere wheels — NOT a fused stand-in model,
			// and says so loudly. The kit builds that blockout when this returns false.
			Log.Error( $"[vp] part kit '{def.BodyManifest}' failed to assemble for '{def.Name}' "
				+ "— spawning primitive blockout (no fused fallback)" );
			return false;
		}

		_pendingKit = kit;
		_pendingWheelModels = wheelModels;
		_pendingKitBody = true;
		return true;
	}

	/// <summary>The kit id a manifest path names — the folder under models/vehicles/
	/// ("models/vehicles/hatch_kit/manifest.json" -> "hatch_kit"). Used by the spawn-identity audit
	/// to confirm the assembled manifest is the one the definition requested.</summary>
	static string KitIdFromPath( string manifestPath )
	{
		if ( string.IsNullOrEmpty( manifestPath ) )
			return "";
		var parts = manifestPath.Replace( '\\', '/' ).Split( '/' );
		// .../<kit_id>/manifest.json -> the segment before the file name
		return parts.Length >= 2 ? parts[^2] : "";
	}
}
