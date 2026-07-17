using Sandbox;
using Sandbox.UI;
using System.Collections.Generic;

namespace VehicleProto;

/// <summary>
/// One car-picker tile's 3D preview: a razor <see cref="ScenePanel"/> that renders its own
/// PRIVATE off-screen <see cref="Scene"/> holding the car's assembled part-kit body + wheels as
/// plain render <see cref="ModelRenderer"/>s, framed by the kit's bounds and slowly yaw-spinning
/// (owner: "show a small rotating 3D model of each vehicle" instead of a static PNG).
///
/// PREVIEW PATTERN (the current, non-obsolete path): a detached
/// <see cref="Scene.CreateEditorScene"/> populated with REAL GameObjects/Components (a
/// <see cref="CameraComponent"/> marked <c>IsMainCamera</c>, two <see cref="PointLight"/>s and an
/// <see cref="AmbientLight"/>, plus the car part renderers), assigned to
/// <see cref="ScenePanel.RenderScene"/>. Never use the removal-imminent
/// <c>ScenePanel.World</c>/<c>Camera</c>/<c>SceneModel</c> raw-SceneObject API. An editor scene does
/// not tick on its own, so <see cref="Scene.EditorTick"/> is pumped each frame in <see cref="Tick"/>.
///
/// ASSEMBLY: the visual mirrors <see cref="PartKitAssembler"/>'s read path but with NO physics —
/// body parts hang under a "kit body" GO rotated by <see cref="PartKitManifest.ModelToRootYaw"/> at
/// each part's <see cref="PartKitPart.AttachLocalM"/>; wheel meshes sit in the root frame at their
/// authored hub (<see cref="PartKitPart.AttachRootM"/>) with the axle-alignment yaw. No colliders,
/// no Rigidbody, no suspension/WheelVisual — purely renderers. The whole car yaws about the vertical
/// axis through the footprint centre (authoring invariant: chassis pivot = footprint-centre@ground,
/// wheels symmetric about it) so it stays centred under the fixed camera.
///
/// DECLARATIVE (gotcha: a razor COLLECTION of stateful child panels can't @ref cleanly): driven
/// entirely by markup attributes (<see cref="CarId"/>, <see cref="Selected"/>) which SessionMenu.razor
/// sets per tile; the panel reads them in its own <see cref="Tick"/>. Each tile owns its own scene.
///
/// FALLBACK: if the kit manifest/models fail to resolve, the scene stays empty and the tile falls
/// back to the existing <c>ui/cars/&lt;id&gt;.png</c> as a panel background (with a [vp] warning), so
/// a broken kit degrades to the old thumbnail rather than an empty box.
/// </summary>
public sealed class CarThumb : ScenePanel
{
	// Slow, steady presentation spin. The selected tile turns a hair faster so the eye is drawn to
	// the current pick; both stay in the calm 20-30 deg/s band the owner asked for.
	const float SpinSpeedSelected = 30f; // deg/s
	const float SpinSpeedIdle = 22f;     // deg/s

	// ---- declarative inputs (set from razor markup each render) ----
	public string CarId { get; set; }
	public bool Selected { get; set; }

	Scene _scene;          // detached off-screen preview scene (RenderScene)
	GameObject _spinRoot;  // the whole car; yawed each frame
	float _spin;           // accumulated yaw (deg)
	string _builtId;       // CarId currently assembled into the scene
	bool _fellBack;        // true once we've dropped to the PNG for this id (don't retry every frame)

	public CarThumb()
	{
		// A detached editor scene: lives outside the game loop (never game-ticks, never networked)
		// and is what the panel renders. Camera + lights are car-independent so they're built once
		// here; the car itself is built lazily once CarId is known (razor sets it after construction).
		_scene = Scene.CreateEditorScene();

		using ( _scene.Push() )
		{
			// Camera looks back down +X toward the origin (the car's nose faces +X), tilted slightly
			// down. Transparent background so the tile's own subtle dark styling shows behind the car.
			// Placed at a nominal spot here; EnsureBuilt reframes it to the specific car's bounds.
			var camGo = new GameObject( true, "thumb-camera" );
			var cam = camGo.Components.GetOrCreate<CameraComponent>();
			cam.WorldPosition = new Vector3( 300f, 0f, 90f );
			cam.WorldRotation = Rotation.LookAt( (Vector3.Zero - camGo.WorldPosition).Normal, Vector3.Up );
			cam.FieldOfView = 28f;
			cam.ZNear = 1f;
			cam.ZFar = 8000f;
			cam.BackgroundColor = Color.Transparent;
			cam.ClearFlags = ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil;
			cam.EnablePostProcessing = true;
			cam.IsMainCamera = true; // the ScenePanel renders the scene's main camera

			// Warm key + cool fill point lights (the engine preview-widget recipe) + a soft ambient
			// so shadowed sides don't read as black. Positions are in engine units (inches).
			var keyGo = new GameObject( true, "thumb-key" );
			var key = keyGo.Components.GetOrCreate<PointLight>();
			key.WorldPosition = new Vector3( 200f, 160f, 260f );
			key.Radius = 2000f;
			key.LightColor = new Color( 1f, 0.96f, 0.86f ) * 6f;
			key.Shadows = false;

			var fillGo = new GameObject( true, "thumb-fill" );
			var fill = fillGo.Components.GetOrCreate<PointLight>();
			fill.WorldPosition = new Vector3( 140f, -200f, 120f );
			fill.Radius = 2000f;
			fill.LightColor = new Color( 0.78f, 0.84f, 1f ) * 4f;
			fill.Shadows = false;

			var ambGo = new GameObject( true, "thumb-ambient" );
			var amb = ambGo.Components.GetOrCreate<AmbientLight>();
			amb.Color = Color.White * 0.35f;
		}

		RenderScene = _scene;
	}

	/// <summary>(Re)assemble the car when the requested id changes. Builds body parts + wheel meshes
	/// from the kit manifest as plain renderers under a spin root, then frames the camera on the
	/// combined bounds. On any manifest/model failure, falls back to the PNG thumbnail.</summary>
	void EnsureBuilt()
	{
		if ( _builtId == CarId )
			return; // already built (or already fell back) for this id

		_builtId = CarId;
		_fellBack = false;

		// tear down any previous car
		if ( _spinRoot.IsValid() )
			_spinRoot.Destroy();
		_spinRoot = null;
		_spin = 0f;
		Style.BackgroundImage = null;

		if ( string.IsNullOrEmpty( CarId ) || !_scene.IsValid() )
			return;

		var def = StationCarRegistry.ResolveCar( CarId );
		var kit = PartKitManifest.TryLoad( def?.PartKitManifest );
		if ( kit is null )
		{
			FallBackToPng( $"kit manifest '{def?.PartKitManifest}' did not resolve" );
			return;
		}

		float m = Units.MetersToUnits;

		using ( _scene.Push() )
		{
			var spinRoot = new GameObject( true, $"thumb-{CarId}" );
			int built = 0;

			try
			{
				// ── body parts: under a "kit body" GO yawed so the model-local nose (+Y) faces root
				// +X, exactly like PartKitAssembler. Each body part sits at its model-local attach. ──
				var body = new GameObject( true, "kit-body" );
				body.SetParent( spinRoot, false );
				body.LocalRotation = PartKitManifest.ModelToRootYaw;

				foreach ( var part in kit.parts )
				{
					if ( part.kind == "wheel" )
						continue;

					var model = Model.Load( part.vmdl );
					if ( model is null || model.IsError )
					{
						if ( part.IsRequired )
							throw new System.Exception( $"required part '{part.part}' model '{part.vmdl}' failed to load" );
						continue; // optional cosmetic part absent — skip, same as the assembler
					}

					var go = new GameObject( true, $"part-{part.part}" );
					go.SetParent( body, false );
					go.LocalPosition = part.AttachLocalM * m;
					go.LocalRotation = Rotation.Identity;
					go.Components.GetOrCreate<ModelRenderer>().Model = model;
					built++;
				}

				// ── wheels: mesh pivot is the hub centre, authored in the root frame; place at the
				// authored hub with the axle-alignment yaw (mirror flag). No suspension/spin here. ──
				foreach ( var wn in PartKitManifest.WheelNames )
				{
					var wpart = kit.Find( wn );
					if ( wpart is null )
						continue;

					var wmodel = Model.Load( wpart.vmdl );
					if ( wmodel is null || wmodel.IsError )
						throw new System.Exception( $"wheel '{wn}' model '{wpart.vmdl}' failed to load" );

					var wgo = new GameObject( true, $"wheel-{wn}" );
					wgo.SetParent( spinRoot, false );
					wgo.LocalPosition = wpart.AttachRootM * m;
					wgo.LocalRotation = Rotation.FromYaw( wpart.mirror ? -90f : 90f );
					wgo.Components.GetOrCreate<ModelRenderer>().Model = wmodel;
					built++;
				}
			}
			catch ( System.Exception e )
			{
				spinRoot.Destroy();
				FallBackToPng( e.Message );
				return;
			}

			if ( built == 0 )
			{
				spinRoot.Destroy();
				FallBackToPng( "no parts assembled" );
				return;
			}

			_spinRoot = spinRoot;
		}

		FrameCamera();

		// Prime one tick so the first rendered frame is the posed car, not a half-initialised scene.
		_scene.EditorTick( RealTime.Now, 0.01f );
	}

	/// <summary>Point the preview camera at the assembled car's combined bounds so every car fills its
	/// tile consistently regardless of size (kart vs pickup). Aims at the footprint-centre vertical
	/// axis (x=y=0 by the authoring invariant) so the yaw-spin never wobbles the framing.</summary>
	void FrameCamera()
	{
		if ( !_spinRoot.IsValid() )
			return;

		// combined world bounds over every part renderer (car root is at identity here, pre-spin)
		BBox bounds = default;
		bool any = false;
		foreach ( var r in _spinRoot.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			var mdl = r.Model;
			if ( mdl is null )
				continue;

			var local = mdl.Bounds;
			var t = r.WorldTransform;
			for ( int c = 0; c < 8; c++ )
			{
				var corner = new Vector3(
					(c & 1) == 0 ? local.Mins.x : local.Maxs.x,
					(c & 2) == 0 ? local.Mins.y : local.Maxs.y,
					(c & 4) == 0 ? local.Mins.z : local.Maxs.z );
				var wp = t.PointToWorld( corner );
				if ( !any ) { bounds = new BBox( wp, wp ); any = true; }
				else bounds = bounds.AddPoint( wp );
			}
		}

		if ( !any )
			return;

		var cam = _scene.Camera;
		if ( cam is null )
			return;

		// Aim at the footprint-centre vertical axis at the car's mid height; radius = bounding sphere.
		float aimZ = bounds.Center.z;
		var center = new Vector3( 0f, 0f, aimZ );
		float radius = bounds.Size.Length * 0.5f;

		const float fov = 28f;
		float e = 15f * MathF.PI / 180f; // slight down-tilt / 3-quarter elevation
		float dist = radius / MathF.Tan( fov * 0.5f * MathF.PI / 180f ) * 1.12f;

		// camera in front (+X) and above, looking back at the car centre
		var dir = new Vector3( MathF.Cos( e ), 0f, MathF.Sin( e ) );
		var camPos = center + dir * dist;

		cam.WorldPosition = camPos;
		cam.WorldRotation = Rotation.LookAt( (center - camPos).Normal, Vector3.Up );
		cam.FieldOfView = fov;
	}

	/// <summary>Degrade this tile to the existing PNG thumbnail when the 3D kit can't be built, so a
	/// broken kit never shows an empty box. Does not touch the PNG assets.</summary>
	void FallBackToPng( string why )
	{
		_fellBack = true;
		Log.Warning( $"[vp] car-thumb '{CarId}': 3D preview unavailable ({why}) — falling back to ui/cars/{CarId}.png" );
		try
		{
			Style.BackgroundImage = Texture.LoadFromFileSystem( $"ui/cars/{CarId}.png", FileSystem.Mounted );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[vp] car-thumb '{CarId}': PNG fallback also failed ({e.Message})" );
		}
	}

	public override void Tick()
	{
		base.Tick();

		if ( !_scene.IsValid() )
			return;

		EnsureBuilt();

		if ( _fellBack || !_spinRoot.IsValid() )
			return;

		float dt = RealTime.Delta;
		_spin += (Selected ? SpinSpeedSelected : SpinSpeedIdle) * dt;
		if ( _spin >= 360f ) _spin -= 360f;
		_spinRoot.WorldRotation = Rotation.FromYaw( _spin );

		// An editor scene has no automatic tick; pump it so transform changes settle for the render.
		_scene.EditorTick( RealTime.Now, dt );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		_spinRoot = null;
		_scene?.Destroy();
		_scene = null;
		RenderScene = null;
	}
}
