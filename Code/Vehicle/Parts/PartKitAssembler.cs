namespace VehicleProto;

/// <summary>
/// Assembles a part-kit vehicle body — separate part GameObjects parented
/// to the chassis root — from a <see cref="PartKitManifest"/>. The raycast-wheel physics
/// core is UNCHANGED: one chassis Rigidbody, the same def-driven root BoxCollider (bump-stop
/// semantics identical to the def-driven baseline), wheels still pure raycast with NO colliders.
/// Stage A adds: per-part render GOs, code-side BoxColliders sized from manifest dims
/// (render-only vmdls have zero engine collision by design), and wheel meshes that mirror the
/// live suspension state through the proven <see cref="WheelVisual"/> component.
///
/// Hierarchy:
///   root (Rigidbody, VehicleController, def collider)          ← physics frame, +X fwd
///   ├── "Kit Body"  LocalRotation = FromYaw(-90)               ← ONE frame fix (proven in kit assembly: nose at model +Y)
///   │   ├── chassis_shell / doors / hood / trunk / bumpers     ← at AuthorToLocal(attach)·39.37
///   ├── Wheel FL..RR (VehicleWheel; steer yaw set by controller)
///   │   └── "Visual" (WheelVisual: bob = SuspensionLength, spin = FromPitch(-spin))
///   │       └── "Mesh" (wheel.vmdl, FromYaw(±90) axle alignment only)
///
/// Stage-C seam: every hinged/bolt-on part is ONE GameObject with its pivot at its joint and
/// its own collider — a future impulse-threshold break detaches it by reparenting + adding a
/// Rigidbody with mass_fraction × def.Mass. Nothing else moves.
/// </summary>
public static class PartKitAssembler
{
	/// <summary>
	/// Build the non-wheel body. Body parts are MANIFEST-DRIVEN (every part whose kind is not
	/// "wheel" mounts rigid under the kit-body GO — doors/hood/tailgate rigid for now, Stage C
	/// makes the hinged ones breakable), so new kits (pickup: bed/tailgate/grille/mirrors/
	/// accessory) need zero assembler edits; kind stays a Stage-C semantic tag. Returns false
	/// (and logs why) on any manifest/model failure so <see cref="VehicleFactory"/> falls
	/// through to the blockout path — a broken kit must never brick a spawn.
	/// </summary>
	public static bool TryBuildBody( Scene scene, GameObject root, CarDefinition def,
		out PartKitManifest kit, out Dictionary<string, Model> wheelModels )
	{
		wheelModels = new Dictionary<string, Model>();
		kit = PartKitManifest.TryLoad( def.PartKitManifest );
		if ( kit is null )
			return false;

		var bodyParts = kit.parts.Where( p => p.kind != "wheel" ).ToList();

		// ── Phase 1: PRELOAD + VALIDATE every REQUIRED body model BEFORE creating any GameObject.
		// TRANSACTIONAL CONTRACT (audit 2026-07-12): a partial body is never acceptable. The old
		// code created part GOs as it went and returned `built > 0`, so a single loaded part out
		// of a dozen counted as success and shipped an incomplete render/collision assembly. Now
		// any REQUIRED part whose vmdl is missing/errored aborts with ZERO objects created and
		// returns false, so VehicleFactory falls through to the blockout path exactly as the
		// comments promise. Optional cosmetic parts (mirror/accessory, or any part with an explicit
		// "required": false) may be absent without downgrading the whole kit — they are skipped.
		var loaded = new Dictionary<PartKitPart, Model>();
		var skippedOptional = new List<string>();
		foreach ( var part in bodyParts )
		{
			var model = Model.Load( part.vmdl );
			if ( model is null || model.IsError )
			{
				if ( part.IsRequired )
				{
					Log.Warning( $"[vp] partkit '{kit.kit}': REQUIRED part '{part.part}' model '{part.vmdl}' failed to load — " +
						"aborting kit with nothing built, falling back to blockout (asset not compiled yet?)" );
					return false;
				}
				Log.Warning( $"[vp] partkit '{kit.kit}': optional part '{part.part}' model '{part.vmdl}' failed to load — skipped" );
				skippedOptional.Add( part.part );
				continue;
			}
			loaded[part] = model;
		}

		if ( loaded.Count == 0 )
		{
			Log.Warning( $"[vp] partkit '{kit.kit}': no body parts loaded — falling back to blockout" );
			return false;
		}

		// ── Phase 1b: PRELOAD the four REQUIRED wheel models INTO THE SAME TRANSACTION (audit
		// 2026-07-13 HIGH). Previously wheel models loaded one-at-a-time in MountWheelVisual AFTER the
		// body committed, so a wheel .vmdl that was absent/errored/not-yet-compiled produced a committed
		// kit with an INVISIBLE wheel (physics intact, so it reads as a suspension bug). Now a missing
		// required wheel aborts the whole kit here (→ blockout fallback) with nothing built, and
		// the loaded models are handed to mounting so it never reloads. Validate() guarantees exactly
		// one of each FL/FR/RL/RR part, so Find never returns null here.
		foreach ( var wn in PartKitManifest.WheelNames )
		{
			var wpart = kit.Find( wn );
			var wmodel = Model.Load( wpart.vmdl );
			if ( wmodel is null || wmodel.IsError )
			{
				Log.Warning( $"[vp] partkit '{kit.kit}': REQUIRED wheel '{wn}' model '{wpart.vmdl}' failed to load — " +
					"aborting kit with nothing built, falling back to blockout (asset not compiled yet?)" );
				return false;
			}
			wheelModels[wn] = wmodel;
		}

		// ── Phase 2: COMMIT. Every required model is in hand; create the GameObjects now. Guarded
		// so that even an unexpected engine error mid-build tears the partial hierarchy back down
		// and returns false, rather than leaving a half-assembled vehicle (never brick a spawn).
		float m = Units.MetersToUnits;

		// The chassis mesh pivot is footprint-centre@GROUND. The body GO is rigid to the root,
		// so it sits at the STATIC-EQUILIBRIUM ground offset — the same math that seats spawns
		// (VehiclePilot.SeatHeightM, live scene gravity) so mesh and physics can never disagree.
		float seatM = VehiclePilot.SeatHeightM( def );

		GameObject body = null;
		try
		{
			body = scene.CreateObject();
			body.Name = "Kit Body";
			body.SetParent( root, false );
			body.LocalRotation = PartKitManifest.ModelToRootYaw;
			body.LocalPosition = Vector3.Down * seatM * m;

			int built = 0, boundsOffenders = 0;
			foreach ( var part in bodyParts )
			{
				if ( !loaded.TryGetValue( part, out var model ) )
					continue; // optional part skipped in phase 1

				var go = scene.CreateObject();
				go.Name = $"Part {part.part}";
				go.SetParent( body, false );
				go.LocalPosition = part.AttachLocalM * m; // chassis model-local frame (see PartKitManifest.AuthorToLocal)
				go.LocalRotation = Rotation.Identity;     // hinge axes stay part-local per manifest rotation_axis_local

				var renderer = go.Components.Create<ModelRenderer>();
				renderer.Model = model;

				// Code-side collision from manifest dims (kit vmdls are deliberately render-only).
				// Colliders on child GOs compound onto the root Rigidbody = rigid-to-body, and
				// BoxCollider specs are authored in the part's own frame (colliders live in
				// the model frame and rotate with the GO — never pre-rotate them).
				var collider = go.Components.Create<BoxCollider>();
				collider.Scale = part.DimsM * m;
				collider.Center = part.BoundsCenterM * m;

				// live cross-check: compiled model bounds vs manifest dims (verifies import_scale
				// and that the vmdl actually compiled from the same OBJ the manifest measured)
				if ( !BoundsMatch( model, part ) )
					boundsOffenders++;

				built++;
			}

			Log.Info( $"[vp] partkit '{kit.kit}' body assembled: {built}/{bodyParts.Count} parts"
				+ ( skippedOptional.Count > 0 ? $" ({skippedOptional.Count} optional skipped: {string.Join( ", ", skippedOptional )})" : "" )
				+ $", seatZ={seatM:F3}m" );
			Log.Info( $"[vp] AUDIT partkit_bounds offenders={boundsOffenders} target 0" );

			// Full crash/destruction simulation is out of scope for this prototyping kit: there is no
			// runtime impact router or part-mesh rebuilder bound here. The manifest carries only inert
			// damage bands (data, no deformation). A kit car assembles identically — it just does not crumple.
			return true;
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[vp] partkit '{kit.kit}': body assembly threw ({e.Message}) — tearing down partial body, falling back to blockout" );
			body?.Destroy();
			return false;
		}
	}

	/// <summary>
	/// Mount one wheel part's mesh on its live physics wheel. Architecture (the "tires bounce"
	/// heart): the mesh goes UNDER the wheel GameObject, so all three state channels mirror the
	/// simulation with zero new code paths —
	///   steer:    VehicleController writes FromYaw(SteerAngle) onto the wheel GO (fronts);
	///   bob:      WheelVisual translates the Visual GO down by live SuspensionLength;
	///   spin:     WheelVisual composes FromPitch(-spinDeg) — Visual base = identity, so the
	///             spin axis is the wheel GO's own Y (the axle) on BOTH sides (world-correct;
	///             the old fused path's yaw-180 right-wheel base reversed apparent spin, invisible
	///             only because those meshes are symmetric).
	/// The Mesh child below carries ONLY the static axle alignment (model axle = local X per
	/// manifest rotation_axis_local → ±90° yaw puts it on the parent's Y, hub outboard by the
	/// mirror flag), so it never fights the animated rotation above it.
	/// WheelVisual is reused UNCHANGED rather than a parallel PartWheelVisual — decision log in
	/// docs/part-kit-assembly.md §4.
	/// </summary>
	public static bool MountWheelVisual( Scene scene, GameObject wheelGo, VehicleWheel wheel,
		PartKitManifest kit, CarDefinition def, bool front, bool left,
		IReadOnlyDictionary<string, Model> preloaded )
	{
		string name = $"wheel_{(front ? 'f' : 'r')}{(left ? 'l' : 'r')}";
		var part = kit.Find( name );
		if ( part is null )
		{
			// Validate guarantees the FL/FR/RL/RR set, so this is unreachable in practice — treat any
			// slip as a visible-blockout offender, never an invisible wheel.
			Log.Warning( $"[vp] partkit '{kit.kit}': manifest has no part '{name}' — blockout wheel + audit offender" );
			VehicleFactory.BuildBlockoutWheelVisual( scene, wheelGo, wheel, def );
			return false;
		}

		// Use the model preloaded in the kit transaction (audit 2026-07-13 HIGH). Belt-and-braces: if a
		// preloaded model is somehow missing/errored despite prevalidation, retry once, and only then
		// drop a VISIBLE blockout wheel + count an offender rather than leaving the wheel invisible.
		Model model = null;
		preloaded?.TryGetValue( name, out model );
		if ( model is null || model.IsError )
			model = Model.Load( part.vmdl );
		if ( model is null || model.IsError )
		{
			Log.Warning( $"[vp] partkit '{kit.kit}': wheel model '{part.vmdl}' failed to load post-commit — blockout wheel + audit offender" );
			VehicleFactory.BuildBlockoutWheelVisual( scene, wheelGo, wheel, def );
			return false;
		}

		// def/kit alignment audit: the physics attach (def wheelbase/track) must sit where the
		// kit authored the hub, or meshes ride outside the arches. Authoring frame == root
		// frame in XY, so this is a direct compare. Warn > 1 cm (kit-matched defs give 0).
		var authored = part.AttachRootM;
		var physics = new Vector3( (front ? 1f : -1f) * def.Wheelbase * 0.5f,
			(left ? 1f : -1f) * def.TrackWidth * 0.5f, 0f );
		if ( MathF.Abs( authored.x - physics.x ) > 0.01f || MathF.Abs( authored.y - physics.y ) > 0.01f )
			Log.Warning( $"[vp] partkit '{kit.kit}': {name} authored hub ({authored.x:F3},{authored.y:F3})m " +
				$"!= physics attach ({physics.x:F3},{physics.y:F3})m — def geometry drifted from kit spec" );

		// spin/bob carrier: identity base so WheelVisual's proven FromPitch spin is about the axle
		var visualGo = scene.CreateObject();
		visualGo.Name = "Visual";
		visualGo.SetParent( wheelGo, false );

		var visual = visualGo.Components.Create<WheelVisual>();
		visual.Wheel = wheel;

		// static axle alignment only: manifest wheel frame has the axle on local X (hub-centre
		// pivot); ±90° yaw lays it on the parent's Y with the hub face outboard (mirror flag —
		// symmetric tyre today, future asymmetric rims ready)
		var meshGo = scene.CreateObject();
		meshGo.Name = "Mesh";
		meshGo.SetParent( visualGo, false );
		meshGo.LocalRotation = Rotation.FromYaw( part.mirror ? -90f : 90f );

		// self-correcting scale from the manifest contract (1.0 when def matches the kit spec)
		float meshRadius = part.dims_m[1] * 0.5f; // dims Y = diameter in the wheel's model frame
		if ( meshRadius > 0.001f && MathF.Abs( meshRadius - def.WheelRadius ) > 0.001f )
		{
			meshGo.LocalScale = Vector3.One * (def.WheelRadius / meshRadius);
			Log.Warning( $"[vp] partkit '{kit.kit}': {name} mesh r={meshRadius:F3}m != def r={def.WheelRadius:F3}m — visual scaled" );
		}

		var renderer = meshGo.Components.Create<ModelRenderer>();
		renderer.Model = model;

		if ( !BoundsMatch( model, part ) )
			Log.Warning( $"[vp] partkit '{kit.kit}': {name} compiled bounds {model.Bounds.Size} != manifest dims — check import_scale" );

		return true;
	}

	/// <summary>Compiled model bounds ≈ manifest dims (±5% + 1u slack)? Live proof the vmdl the
	/// engine loaded matches the geometry the manifest measured (import_scale, right OBJ).</summary>
	static bool BoundsMatch( Model model, PartKitPart part )
	{
		var expect = part.DimsM * Units.MetersToUnits;
		var got = model.Bounds.Size;
		for ( int i = 0; i < 3; i++ )
		{
			float e = expect[i], g = got[i];
			if ( MathF.Abs( g - e ) > MathF.Max( e * 0.05f, 1f ) )
			{
				Log.Warning( $"[vp] partkit bounds mismatch '{part.part}': model {got} vs manifest {expect} (axis {i})" );
				return false;
			}
		}
		return true;
	}
}
