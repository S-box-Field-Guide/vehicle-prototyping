namespace VehicleProto;

/// <summary>
/// Builds the proving-grounds test-track zone (see docs/proving-grounds.md): skidpad, drag strip + brake
/// zone, slalom, ramp set, banked curve, washboard, hill grade ladder, low-grip patch,
/// J-turn pad, and a reserved crash-wall plot. Built from a test-track spawner idiom and
/// extended with the additional proving-ground stations, using primitive-helper
/// idioms (Block/Ramp/PlaceModel, static box colliders, dev-box models, deterministic
/// layout — no runtime RNG).
///
/// All station math is authored in SI METERS relative to a caller-supplied origin (the
/// track zone sits ~600 m east of world origin, but that offset is a
/// parameter here, never hardcoded). Every GameObject placement funnels through the
/// <see cref="Slab"/>/<see cref="Cone"/> helpers, which are the ONLY two places that
/// apply <c>Units.MetersToUnits</c> — that is the single audited engine-boundary
/// conversion point (the units convention is"Units, axes, and frames": audit EVERY
/// consumer for the missed `* M` trap). <see cref="Stations"/> itself stores SI meters,
/// NOT engine units — the eventual vp_spawn consumer converts at ITS own placement call.
///
/// UNVERIFIED APIs used here (this file was written without a compiler — the csproj and
/// VehicleProto namespace landed concurrently). See the "Uncertain
/// APIs" list at the top of docs/proving-grounds.md for the full rundown and what to check
/// at integration.
/// </summary>
public static class TestTrack
{
	const float M = Units.MetersToUnits;

	// rough triangle-count estimates for the census log — NOT exact, just order-of-magnitude
	// (dev box = 12 tris/cube face pairs; dev plane = 2 tris; custom cone ~90 tris)
	const int BoxTrisEstimate = 12;
	const int PlaneTrisEstimate = 2;
	const int ConeTrisEstimate = 90;

	static Scene _scene;
	static GameObject _root;
	static Vector3 _origin; // meters — the world-relative offset this Build() call was given

	static int _boxCount;
	static int _planeCount;
	static int _modelCount;

	/// <summary>
	/// Named spawn points, keyed by station name (see docs/proving-grounds.md for the full
	/// table). Positions are SI METERS in world space (origin already applied); facing is a
	/// ready-to-use world Rotation. Populated by <see cref="Build"/>; empty until then.
	/// </summary>
	public static IReadOnlyDictionary<string, (Vector3 posMeters, Rotation facing)> Stations { get; private set; }
		= new Dictionary<string, (Vector3, Rotation)>();

	/// <summary>
	/// Builds the whole test-track zone under a fresh root GameObject, offset by
	/// <paramref name="originMeters"/> from world origin (SI meters). Deterministic — no
	/// runtime RNG, safe to call once per session boot. Does not touch Vehicle/UI/Game code;
	/// the bootstrap calls this from GameBootstrap after CityBuilder.Build (see doc).
	/// </summary>
	public static void Build( Scene scene, Vector3 originMeters )
	{
		_scene = scene;
		_origin = originMeters;
		_root = scene.CreateObject();
		_root.Name = "Proving Grounds";

		_boxCount = 0;
		_planeCount = 0;
		_modelCount = 0;

		var stations = new Dictionary<string, (Vector3 posMeters, Rotation facing)>();

		BuildGround();
		BuildSkidpad( stations );
		BuildDragStripAndBrakeZone( stations );
		BuildSlalom( stations );
		BuildRamps( stations );
		BuildBankedCurve( stations );
		BuildWashboard( stations );
		BuildHillLadder( stations );
		BuildLowGripPatch( stations );
		BuildJTurnPad( stations );
		BuildCrashWallReserve( stations );

		Stations = stations;

		long trisEstimate = (long)_boxCount * BoxTrisEstimate
			+ (long)_planeCount * PlaneTrisEstimate
			+ (long)_modelCount * ConeTrisEstimate;

		Log.Info( $"[vp] world track stations={stations.Count} tris~={trisEstimate}" );
	}

	// ---------------------------------------------------------------- ground

	static void BuildGround()
	{
		// station footprints span local X[35,815] Y[-170,220] (see docs/proving-grounds.md
		// ASCII map) — center + span chosen with generous margin, the same "oversized ground
		// BoxCollider" idiom.
		// COUPLING (world pass 2026-07-19): Outskirts wraps this slab's north/south/east CLIFF
		// edges with sunken run-off aprons and stands the outer world perimeter at their rim —
		// its TrackWest/East/South/North datums mirror this centre+span. Move this ground, move
		// those. The cliff edges themselves are load-bearing: TopSpeedManeuver ends on the
		// contact-loss of running off the east edge.
		var center = new Vector3( 400f, 20f, 0f );
		const float spanXMeters = 1000f;
		const float spanYMeters = 600f;

		var go = Child( "Track Ground" );
		go.WorldPosition = ( _origin + center ) * M;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/plane.vmdl" );
		renderer.Tint = new Color( 0.45f, 0.44f, 0.40f ); // dry proving-ground hardpack, distinct from the city's grass
		// dev plane is a 100x100-unit quad; LocalScale is a raw multiplier, not meters —
		// scale = desiredSpanMeters * M / 100
		go.LocalScale = new Vector3( spanXMeters * M / 100f, spanYMeters * M / 100f, 1f );
		_planeCount++;

		// thick slab: hard landings (this zone HAS ramps + a jump station) must not tunnel
		// through a thin ground collider; BoxCollider follows WorldScale so this stretches correctly with the
		// non-square span above
		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 100f, 100f, 200f );
		collider.Center = Vector3.Down * 100f;
		collider.Static = true;

		go.Tags.Add( "road" );
	}

	// ---------------------------------------------------------------- skidpad

	static void BuildSkidpad( Dictionary<string, (Vector3, Rotation)> stations )
	{
		var center = new Vector3( 60f, 150f, 0.02f );
		const float radius = 20f;
		const int ringMarkers = 32;
		var ringColor = new Color( 0.92f, 0.90f, 0.85f );

		for ( int i = 0; i < ringMarkers; i++ )
		{
			float a = i * MathF.Tau / ringMarkers;
			var offset = new Vector3( MathF.Cos( a ), MathF.Sin( a ), 0f ) * radius;
			Block( center + offset, new Vector3( 0.8f, 0.8f, 0.05f ), ringColor, collide: false, name: "Skidpad Ring Marker" );
		}

		// spawn on the west edge of the ring, facing tangent (+Y) so the car curls onto the circle
		stations["skidpad"] = ( StationPos( new Vector3( center.x - radius, center.y, 0f ) ), Rotation.FromYaw( 90f ) );
	}

	// ---------------------------------------------------------------- drag strip + brake zone

	static void BuildDragStripAndBrakeZone( Dictionary<string, (Vector3, Rotation)> stations )
	{
		const float startX = 150f, laneY = 40f, laneWidth = 14f, dragLength = 400f, brakeZoneLength = 100f;
		var asphalt = new Color( 0.30f, 0.30f, 0.33f );
		var lineColor = new Color( 0.95f, 0.85f, 0.30f );

		float padLength = dragLength + brakeZoneLength;
		Block( new Vector3( startX + padLength * 0.5f, laneY, 0.01f ), new Vector3( padLength, laneWidth, 0.02f ),
			asphalt, collide: false, name: "Drag Strip + Brake Zone Pad" );

		Block( new Vector3( startX, laneY, 0.02f ), new Vector3( 0.4f, laneWidth, 0.03f ), lineColor, collide: false, name: "Start Line" );

		// distance boards at 100/200/300/400 m — painted line across the lane plus a standing
		// marker board OFF to the side (never in the driving line)
		for ( int i = 1; i <= 4; i++ )
		{
			float bx = startX + i * 100f;
			Block( new Vector3( bx, laneY, 0.02f ), new Vector3( 0.4f, laneWidth, 0.03f ), lineColor, collide: false, name: $"{i * 100}m Line" );
			Block( new Vector3( bx, laneY + laneWidth * 0.5f + 1.5f, 1f ), new Vector3( 0.3f, 0.3f, 2f ),
				new Color( 0.85f, 0.15f, 0.10f ), collide: false, name: $"{i * 100}m Board" );
		}

		float brakeEntryX = startX + dragLength;

		// brake-zone entry gate: side pillars only, non-colliding — must never block the lane
		Block( new Vector3( brakeEntryX, laneY - laneWidth * 0.5f - 1f, 1.2f ), new Vector3( 0.6f, 0.6f, 2.4f ),
			new Color( 0.85f, 0.2f, 0.15f ), collide: false, name: "Brake Entry Marker" );
		Block( new Vector3( brakeEntryX, laneY + laneWidth * 0.5f + 1f, 1.2f ), new Vector3( 0.6f, 0.6f, 2.4f ),
			new Color( 0.85f, 0.2f, 0.15f ), collide: false, name: "Brake Entry Marker" );

		foreach ( var d in new[] { 20f, 50f, 80f } )
		{
			float bx = brakeEntryX + d;
			Block( new Vector3( bx, laneY + laneWidth * 0.5f + 1.5f, 1f ), new Vector3( 0.3f, 0.3f, 1.6f ),
				new Color( 0.2f, 0.35f, 0.85f ), collide: false, name: $"Brake {d:0}m Board" );
		}

		stations["dragstrip"] = ( StationPos( new Vector3( startX - 10f, laneY, 0f ) ), Rotation.Identity );
		stations["brakezone"] = ( StationPos( new Vector3( brakeEntryX - 30f, laneY, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- slalom

	static void BuildSlalom( Dictionary<string, (Vector3, Rotation)> stations )
	{
		const float startX = 150f, laneY = -40f, spacing = 18f, lateral = 2.5f;
		const int coneCount = 8;
		var coneColor = new Color( 0.95f, 0.45f, 0.05f );

		for ( int i = 0; i < coneCount; i++ )
		{
			float x = startX + i * spacing;
			float y = laneY + ( i % 2 == 0 ? lateral : -lateral );
			ConeOrBlock( new Vector3( x, y, 0f ), coneColor, $"Slalom Cone {i + 1}" );
		}

		stations["slalom"] = ( StationPos( new Vector3( startX - spacing, laneY, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- ramp set

	static void BuildRamps( Dictionary<string, (Vector3, Rotation)> stations )
	{
		const float laneY = -150f;
		var rampColor = new Color( 0.55f, 0.55f, 0.6f );
		var apronColor = new Color( 0.35f, 0.35f, 0.38f );

		var ramps = new (float startX, float pitchDeg, float length, float width)[]
		{
			( 60f, 10f, 10f, 6f ),   // small
			( 140f, 16f, 12f, 6f ),  // medium
			( 220f, 22f, 14f, 7f ),  // large
		};

		foreach ( var r in ramps )
		{
			// Curved SOLID kicker (RampKicker): base tangent to grade (no lip), collision following the
			// curved face, sealed underside (no drive-under gap). Base sits at startX, runs +X to the lip
			// at startX+length — same footprint as the old straight box wedge; height ~= the old crest.
			// NOTE: the hill-grade ladder (BuildHillLadder) deliberately KEEPS the straight Ramp() — a
			// grade test is a constant-slope surface and HillClimbManeuver measures the crest off it.
			float heightM = MathF.Sin( r.pitchDeg.DegreeToRadian() ) * r.length * 0.25f;
			RampKicker.Build( _scene, _root, _origin + new Vector3( r.startX, laneY, 0f ), 0f,
				r.length, r.width, heightM, rampColor );

			float apronStartX = r.startX + r.length + 2f;
			Block( new Vector3( apronStartX + 5f, laneY, 0.01f ), new Vector3( 10f, r.width + 2f, 0.05f ),
				apronColor, collide: false, name: "Landing Apron" );
		}

		stations["ramps"] = ( StationPos( new Vector3( 30f, laneY, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- banked curve

	static void BuildBankedCurve( Dictionary<string, (Vector3, Rotation)> stations )
	{
		var arcCenter = new Vector2( 700f, 220f );
		const float radius = 45f;
		const float startAngleDeg = 180f, sweepDeg = 90f;
		const int segments = 9;
		const float bankDeg = 18f;
		const float laneWidth = 12f;
		var color = new Color( 0.42f, 0.42f, 0.46f );

		float segAngleDeg = sweepDeg / segments;
		float segLength = radius * segAngleDeg.DegreeToRadian() * 1.15f; // slight overlap so segments don't gap

		for ( int i = 0; i < segments; i++ )
		{
			float midAngleDeg = startAngleDeg + ( i + 0.5f ) * segAngleDeg;
			float midAngleRad = midAngleDeg.DegreeToRadian();
			var pos = arcCenter + new Vector2( MathF.Cos( midAngleRad ), MathF.Sin( midAngleRad ) ) * radius;

			// tangent heading for increasing angle (counterclockwise travel around the arc
			// center) — sign/direction not verified against actual steering convention, see
			// docs/proving-grounds.md uncertainty list
			float yawDeg = midAngleDeg + 90f;

			BankedSegment( new Vector3( pos.x, pos.y, 0f ), new Vector3( segLength, laneWidth, 0.4f ), yawDeg, bankDeg, color,
				name: $"Banked Segment {i + 1}" );
		}

		float entryAngleRad = startAngleDeg.DegreeToRadian();
		var entryPos = arcCenter + new Vector2( MathF.Cos( entryAngleRad ), MathF.Sin( entryAngleRad ) ) * ( radius + 8f );
		stations["bankedcurve"] = ( StationPos( new Vector3( entryPos.x, entryPos.y, 0f ) ), Rotation.FromYaw( startAngleDeg + 90f ) );
	}

	// ---------------------------------------------------------------- washboard

	static void BuildWashboard( Dictionary<string, (Vector3, Rotation)> stations )
	{
		const float laneY = -150f, startX = 320f, spacing = 1.5f, laneWidth = 12f;
		const int ridgeCount = 20;
		var ridgeColor = new Color( 0.40f, 0.36f, 0.30f );

		Block( new Vector3( startX + ridgeCount * spacing * 0.5f, laneY, 0.005f ),
			new Vector3( ridgeCount * spacing + 4f, laneWidth, 0.02f ), new Color( 0.30f, 0.30f, 0.33f ),
			collide: false, name: "Washboard Pad" );

		for ( int i = 0; i < ridgeCount; i++ )
		{
			float x = startX + i * spacing;
			// low transverse ridge: thin along travel (X), full lane width along Y, no rotation needed
			Block( new Vector3( x, laneY, 0.06f ), new Vector3( 0.25f, laneWidth, 0.12f ), ridgeColor, collide: true, name: "Washboard Ridge" );
		}

		stations["washboard"] = ( StationPos( new Vector3( startX - 15f, laneY, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- hill grade ladder

	const float HillLaneYMetersConst = -150f;  // spawn row (the 5% ramp's row)
	const float HillSpawnXMeters = 385f;       // origin-relative X of the hillclimb spawn (the climb datum)
	const float HillBaseXMetersConst = 430f;   // origin-relative X where every ramp's base sits
	const float HillRampLengthMeters = 20f;
	const float HillRowPitchMeters = 14f;      // Y distance between adjacent grade rows (10 m ramps + 4 m gap)

	// NINE DISCRETE graded ramps in PARALLEL ROWS — one grade per row, every base at the same X, rows
	// fanning south from the spawn row. Each ramp starts at ground level so the verified CityBuilder
	// Ramp lift idiom (center rises by sin(pitch)*length*0.25 off a ground-level 'at') applies cleanly
	// per-segment. Extended 2026-07-13 (wave-2) from the old SERIAL 5/10/15/20% ladder for two reasons:
	// (1) 3 of the 4 hillclimb bands (Hatch 25-30, Coupe 35-40, Pickup 40-45) exceeded the 20% ceiling;
	// (2) the serial layout made every car drive OVER all lower ramps en route — each ramp's east edge
	// is an elevated cliff (the 40% crest is ~7 m up), so the first live battery measured an obstacle
	// course, not grade-holding (pickup flipped off a crest drop; wheelspin spiked on landings). The
	// parallel fan gives each grade its own flat approach. SINGLE SOURCE OF TRUTH: exposed via
	// HillLadder so HillClimbManeuver routes to its rated row + detects the crest WITHOUT re-hardcoding
	// the layout (no drift between geometry and the maneuver that measures it).
	static readonly (float laneYMeters, float gradePct)[] _hillGradeRows =
	{
		( -150f, 5f ), ( -164f, 10f ), ( -178f, 15f ), ( -192f, 20f ),
		( -206f, 25f ), ( -220f, 30f ), ( -234f, 35f ), ( -248f, 40f ), ( -262f, 45f ),
	};

	/// <summary>The hill-grade ladder as (origin-relative row Y in SI meters, grade %), grade-ascending.
	/// <see cref="HillClimbManeuver"/> reads this + the spawn/base datums below to route to the rated
	/// grade's row and decide when the car has crested its ramp, so the layout lives in exactly one
	/// place (geometry and the measuring maneuver can't drift apart).</summary>
	public static IReadOnlyList<(float laneYMeters, float gradePct)> HillLadder => _hillGradeRows;

	/// <summary>Origin-relative X (SI meters) of the hillclimb spawn — the datum forward progress is
	/// measured from (paired with <see cref="HillLadder"/>).</summary>
	public static float HillLadderSpawnXMeters => HillSpawnXMeters;

	/// <summary>Origin-relative Y (SI meters) of the spawn row (the 5% ramp's row).</summary>
	public static float HillLaneYMeters => HillLaneYMetersConst;

	/// <summary>Origin-relative X (SI meters) of every ramp's base edge.</summary>
	public static float HillBaseXMeters => HillBaseXMetersConst;

	/// <summary>Along-slope length (SI meters) of each ladder ramp.</summary>
	public static float HillRampLength => HillRampLengthMeters;

	static void BuildHillLadder( Dictionary<string, (Vector3, Rotation)> stations )
	{
		var color = new Color( 0.50f, 0.45f, 0.38f );

		foreach ( var g in _hillGradeRows )
		{
			float pitchDeg = MathF.Atan( g.gradePct / 100f ) * ( 180f / MathF.PI );
			var size = new Vector3( HillRampLengthMeters, 10f, 0.4f );
			var center = new Vector3( HillBaseXMetersConst + size.x * 0.5f, g.laneYMeters, 0f );
			Ramp( center, size, pitchDeg, color, name: $"Hill Grade {g.gradePct:0}%" );
		}

		stations["hillclimb"] = ( StationPos( new Vector3( HillSpawnXMeters, HillLaneYMetersConst, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- low-grip patch

	static void BuildLowGripPatch( Dictionary<string, (Vector3, Rotation)> stations )
	{
		var center = new Vector3( 600f, -40f, 0.02f );
		var size = new Vector3( 25f, 25f, 0.03f );
		var iceBlue = new Color( 0.55f, 0.70f, 0.80f );

		// TODO(surface-friction): visual patch ONLY. No per-surface friction override
		// mechanism exists in the physics stack yet.
		// Hook point for whoever builds it: this GameObject is tagged "low_grip_todo"; either
		// have VehicleWheel's tire-curve sample check for an overlapping tagged volume at
		// contact time, or expose a per-material friction scalar read off the trace surface.
		var go = Block( center, size, iceBlue, collide: false, name: "Low-Grip Patch" );
		go.Tags.Add( "low_grip_todo" );

		stations["lowgrip"] = ( StationPos( new Vector3( center.x - 12f, center.y, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- J-turn pad

	static void BuildJTurnPad( Dictionary<string, (Vector3, Rotation)> stations )
	{
		var center = new Vector3( 780f, 0f, 0.02f );
		const float padSize = 70f;
		var asphalt = new Color( 0.30f, 0.30f, 0.33f );
		var stripe = new Color( 0.95f, 0.85f, 0.30f );

		Block( center, new Vector3( padSize, padSize, 0.03f ), asphalt, collide: false, name: "J-Turn Pad" );

		float half = padSize * 0.5f - 0.5f;
		Block( center + new Vector3( 0f, half, 0.03f ), new Vector3( padSize, 1f, 0.02f ), stripe, collide: false, name: "J-Turn Pad Border" );
		Block( center + new Vector3( 0f, -half, 0.03f ), new Vector3( padSize, 1f, 0.02f ), stripe, collide: false, name: "J-Turn Pad Border" );
		Block( center + new Vector3( half, 0f, 0.03f ), new Vector3( 1f, padSize, 0.02f ), stripe, collide: false, name: "J-Turn Pad Border" );
		Block( center + new Vector3( -half, 0f, 0.03f ), new Vector3( 1f, padSize, 0.02f ), stripe, collide: false, name: "J-Turn Pad Border" );

		stations["jturnpad"] = ( StationPos( center ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- crash wall (reserved plot)

	static void BuildCrashWallReserve( Dictionary<string, (Vector3, Rotation)> stations )
	{
		// REFERENCE-ONLY reserved crash plot. Full crash/destruction simulation (an impact router and
		// runtime deformation) is out of scope for this prototyping kit, so no maneuver drives this plot.
		// It is still built as a reserved spawn point (`crashwall_reserved`): a solid static wall
		// perpendicular to +X travel with a straight west run-up, kept so the station reads clearly and
		// remains a hook point for anyone extending the kit. Deterministic, verified Slab idiom.
		const float wallFaceX = 798f;      // west face of the wall = the frontal contact plane
		const float laneY = -160f;         // clear lane (10 m south of the y=-150 ramp/hill lane)
		const float spawnX = 708f;         // ~90 m run-up west of the wall face
		var hazard = new Color( 0.85f, 0.65f, 0.10f );
		var postColor = new Color( 0.55f, 0.15f, 0.10f );

		// wall: 4 m thick (x), 20 m wide (y), 6 m tall (z), centred just east of the contact face.
		Block( new Vector3( wallFaceX + 2f, laneY, 3f ), new Vector3( 4f, 20f, 6f ), hazard, collide: true, name: "Crash Wall" );

		// hazard posts flanking the wall so the station reads clearly in screenshots
		foreach ( float signY in new[] { 1f, -1f } )
			Block( new Vector3( wallFaceX, laneY + 11f * signY, 1.5f ), new Vector3( 0.5f, 0.5f, 3f ),
				postColor, collide: true, name: "Crash Wall Post" );

		// painted approach centreline so the run-up lane is visible
		Block( new Vector3( (spawnX + wallFaceX) * 0.5f, laneY, 0.02f ), new Vector3( wallFaceX - spawnX, 0.3f, 0.02f ),
			new Color( 0.95f, 0.85f, 0.30f ), collide: false, name: "Crash Approach Line" );

		// spawn ~90 m west of the wall face, nose down +X (Rotation.Identity) straight at the wall.
		stations["crashwall_reserved"] = ( StationPos( new Vector3( spawnX, laneY, 0f ) ), Rotation.Identity );
	}

	// ---------------------------------------------------------------- primitive helpers

	/// <summary>SI meters (local, relative to this Build's origin) → world SI meters.</summary>
	static Vector3 StationPos( Vector3 localMeters ) => _origin + localMeters;

	static GameObject Block( Vector3 posMeters, Vector3 sizeMeters, Color color, bool collide = true, string name = "Block" )
		=> Slab( posMeters, sizeMeters, color, Rotation.Identity, collide, name );

	/// <summary>
	/// Pitched ramp along local +X. The box's near edge rests close to ground while its
	/// CENTER rises by sin(pitch)*length*0.25 off the ground-level position passed in.
	/// </summary>
	static GameObject Ramp( Vector3 posMeters, Vector3 sizeMeters, float pitchDeg, Color color, bool collide = true, string name = "Ramp" )
	{
		float liftMeters = MathF.Sin( pitchDeg.DegreeToRadian() ) * sizeMeters.x * 0.25f;
		var lifted = posMeters + Vector3.Up * liftMeters;
		return Slab( lifted, sizeMeters, color, Rotation.FromPitch( -pitchDeg ), collide, name );
	}

	static GameObject BankedSegment( Vector3 posMeters, Vector3 sizeMeters, float yawDeg, float bankDeg, Color color,
		bool collide = true, string name = "Banked Segment" )
		=> Slab( posMeters, sizeMeters, color, Rotation.From( 0f, yawDeg, bankDeg ), collide, name );

	/// <summary>
	/// The one shared dev-box placement primitive — every Block/Ramp/BankedSegment call
	/// funnels through here, so this plus <see cref="Cone"/> are the ONLY two `* M`
	/// conversion sites in the whole file (audited against the missed-conversion trap).
	/// </summary>
	static GameObject Slab( Vector3 posMeters, Vector3 sizeMeters, Color color, Rotation rotation, bool collide, string name )
	{
		var go = Child( name );
		go.WorldPosition = ( _origin + posMeters ) * M;
		go.WorldRotation = rotation;
		go.LocalScale = sizeMeters * M / 50f; // dev box is a 50x50x50-unit cube

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = color;
		_boxCount++;

		if ( collide )
		{
			var collider = go.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 50f, 50f, 50f );
			collider.Static = true;
		}

		return go;
	}

	static void ConeOrBlock( Vector3 posMeters, Color fallbackColor, string name )
	{
		if ( !Cone( posMeters, name ) )
			Block( posMeters + Vector3.Up * 0.6f, new Vector3( 0.6f, 0.6f, 1.2f ), fallbackColor, collide: true, name: name );
	}

	/// <summary>
	/// Places the custom flat-shaded traffic cone (models/city/cone.vmdl, generated by
	/// tools/gen_buildings.py — retires the Kenney cone), bounds-scaled and rested on the
	/// ground (graceful false-on-missing-model fallback; collider fit to model bounds, NOT
	/// pre-multiplied by scale, since BoxCollider follows the GameObject's WorldScale).
	/// Cones are rotationally symmetric so no yaw parameter is needed.
	/// </summary>
	static bool Cone( Vector3 posMeters, string name )
	{
		var model = Model.Load( "models/city/cone.vmdl" );
		if ( model is null || model.IsError )
			return false;

		var size = model.Bounds.Size;
		if ( size.z <= 0.01f )
			return false;

		const float coneHeightMeters = 0.7f;
		float scale = coneHeightMeters * M / size.z;
		var world = _origin + posMeters;

		var go = Child( name );
		go.WorldPosition = new Vector3( world.x * M, world.y * M, world.z * M - model.Bounds.Mins.z * scale );
		go.LocalScale = Vector3.One * scale;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model; // cone vmdl carries its own orange/white flat vmats — no Tint/MaterialOverride
		_modelCount++;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = model.Bounds.Size;
		collider.Center = model.Bounds.Center;
		collider.Static = true;

		return true;
	}

	static GameObject Child( string name )
	{
		var go = _scene.CreateObject();
		go.Name = name;
		go.SetParent( _root, true );
		return go;
	}
}
