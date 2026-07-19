namespace VehicleProto;

/// <summary>
/// The OUTSKIRTS belt (world pass 2026-07-19): everything in the proto world OUTSIDE the city wall.
/// Before this pass the default world was the 470 m walled city with the proving grounds sitting
/// unreachable 600 m east — players ran out of road in a minute and the test track was
/// teleport-only. The belt makes the whole proto map one continuous drivable space:
///
///   • ground FILLS at grade between the city apron, the track hardpack, and the new outer wall
///   • a 14 m HIGHWAY RING around the city (~590 m straights, chamfered corners, centre dashes)
///   • CONNECTOR roads from each city gate (see CityBuilder.BuildPerimeterWall) to the ring
///   • an east SPUR from the ring to the proving grounds, with a marker gantry
///   • a handful of greybox LANDMARKS in the belt so there is something to drive between
///   • an OUTER PERIMETER sealing the combined world (city + ring + proving grounds)
///
/// BATTERY SAFETY (the reason this file looks the way it does): the proto world is also the
/// measurement instrument, and nothing here may move a station or change a maneuver's outcome.
/// Two rules follow. (1) No new geometry inside the station footprint (world x 610-1500 on the
/// hardpack) except non-colliding paint that ends at x 560. (2) The track ground's CLIFF EDGES are
/// PRESERVED: TopSpeedManeuver ends when the car runs off the east edge of the hardpack into &gt;0.3 s
/// of freefall, so the surround beyond those edges is a SUNKEN RUN-OFF APRON 3 m below grade — a
/// car still falls off the edge exactly as before, but lands in a sealed basin with return ramps
/// instead of the void. The outer wall stands at the apron rim.
///
/// Deterministic: fixed layout, no RNG. All positions authored in SI metres; the Block/Panel
/// helpers are the only unit-conversion sites (house convention).
/// </summary>
public static class Outskirts
{
	const float M = Units.MetersToUnits;

	// ---- layout datums (metres) ----
	// City side: the gates sit in the CityBuilder wall (line at CityBuilder.WallEdge = 242); the
	// city grass plane extends to Total * 1.2 / 2 = 282 and the fills tuck 2 m under its edge.

	// Track side: mirrors TestTrack.BuildGround (centre (400,20)+origin(600,0), span 1000x600).
	// If the track ground ever moves, these four MUST move with it (drift hazard, noted there too).
	const float TrackWest = 500f;
	const float TrackEast = 1500f;
	const float TrackSouth = -280f;
	const float TrackNorth = 320f;

	// Belt/ring/outer-wall lines.
	const float RingR = 296f;          // ring road centreline distance from origin
	const float RingW = 14f;           // highway width (city streets are 10)
	const float OuterWest = -330f;
	const float OuterNS = 330f;        // outer wall at y = ±330
	const float OuterEast = 1558f;     // beyond the track's east cliff + run-off apron

	// Surfaces. Fills sit 5 mm below the surfaces they butt (city grass, track hardpack) so the
	// overlapped seams never z-fight and the exposed boundary is an invisible 5 mm step. Aprons are
	// the sunken run-off basins around the track's cliff edges.
	const float FillTop = -0.005f;
	const float ApronTop = -3f;
	const float SlabThick = 3f;        // fills/aprons are thick solids — hard landings cannot tunnel

	static readonly Color Scrub = new( 0.58f, 0.62f, 0.46f );      // belt ground, drier than city grass
	static readonly Color ApronGrey = new( 0.40f, 0.40f, 0.38f );  // basin floor
	static readonly Color Asphalt = new( 0.30f, 0.30f, 0.33f );
	static readonly Color LaneDash = new( 0.9f, 0.85f, 0.55f );
	static readonly Color WallGrey = new( 0.50f, 0.50f, 0.54f );
	static readonly Color CapGrey = new( 0.38f, 0.38f, 0.42f );

	static Scene _scene;
	static GameObject _root;

	public static void Build( Scene scene )
	{
		_scene = scene;
		_root = scene.CreateObject();
		_root.Name = "Outskirts";

		BuildFills();
		BuildAprons();
		BuildRing();
		BuildConnectors();
		BuildSpur();
		BuildLandmarks();
		BuildReturnRamps();
		BuildOuterPerimeter();

		Log.Info( "[vp] outskirts built: ring road + 4 city gates + proving-grounds connector, outer perimeter sealed" );
	}

	// ---------------------------------------------------------------- ground fills (at grade)

	static void BuildFills()
	{
		// Non-overlapping regions (2 m tucked UNDER each neighbour at the seams): west of the city
		// grass, east of it up to the track hardpack, and north/south strips between those two.
		Fill( -332f, -280f, -332f, 332f, "Fill W" );
		Fill( 280f, 502f, -332f, 332f, "Fill E" );
		Fill( -280f, 280f, 280f, 332f, "Fill N" );
		Fill( -280f, 280f, -332f, -280f, "Fill S" );
	}

	static void Fill( float x0, float x1, float y0, float y1, string name )
		=> Slab( x0, x1, y0, y1, FillTop, Scrub, name );

	// ---------------------------------------------------------------- run-off aprons (sunken)

	static void BuildAprons()
	{
		// Sunken basins wrapping the track ground's north, south and east CLIFF edges (preserved —
		// see class doc). The three connect into one basin ring, so any car that sails off any edge
		// can drive to a return ramp. Tucked 2-4 m under the track slab / east fill at the seams.
		Slab( 498f, 1498f, TrackNorth - 4f, 332f, ApronTop, ApronGrey, "Runoff Apron N" );
		Slab( 498f, 1498f, -332f, TrackSouth + 4f, ApronTop, ApronGrey, "Runoff Apron S" );
		Slab( 1498f, 1560f, -332f, 332f, ApronTop, ApronGrey, "Runoff Apron E" );
	}

	// ---------------------------------------------------------------- ring road (paint)

	static void BuildRing()
	{
		float span = RingR + RingW * 0.5f;             // N/S strips run corner to corner
		float inner = RingR - RingW * 0.5f;            // E/W strips BUTT the N/S strips (overlapping
		// coplanar paint z-fights, so only the N/S pair spans the corner squares)
		Paint( 0f, RingR, span * 2f, RingW, 0f, "Ring Road N" );
		Paint( 0f, -RingR, span * 2f, RingW, 0f, "Ring Road S" );
		Paint( RingR, 0f, RingW, inner * 2f, 0f, "Ring Road E" );
		Paint( -RingR, 0f, RingW, inner * 2f, 0f, "Ring Road W" );
		// 45° corner chamfers so the racing line flows (slightly above the strips, no z-fight)
		foreach ( var (sx, sy) in new[] { (1f, 1f), (-1f, 1f), (-1f, -1f), (1f, -1f) } )
		{
			var go = PaintGo( sx * 283f, sy * 283f, 42f, RingW, 0.012f, "Ring Corner" );
			go.WorldRotation = Rotation.FromYaw( sx * sy > 0f ? -45f : 45f );
		}
		// centre dashes down each straight
		for ( float t = -270f; t <= 270f; t += 20f )
		{
			Dash( t, RingR, alongX: true );
			Dash( t, -RingR, alongX: true );
			Dash( RingR, t, alongX: false );
			Dash( -RingR, t, alongX: false );
		}
	}

	// ---------------------------------------------------------------- gate connectors + spur

	static void BuildConnectors()
	{
		// city avenue → gate → ring, one per side (the avenues run through the origin, so each
		// connector sits on an axis). Butts the avenue's end exactly (coplanar overlap z-fights).
		float from = CityBuilder.Total * 0.5f;         // the avenue paint ends here
		float to = RingR - RingW * 0.5f;               // butts the ring strip
		float mid = (from + to) * 0.5f, len = to - from;
		Paint( 0f, mid, 10f, len, 0f, "Gate Road N" );
		Paint( 0f, -mid, 10f, len, 0f, "Gate Road S" );
		Paint( mid, 0f, len, 10f, 0f, "Gate Road E" );
		Paint( -mid, 0f, len, 10f, 0f, "Gate Road W" );
	}

	static void BuildSpur()
	{
		// ring east side → the proving-grounds hardpack. Paint only, ending well west of the
		// station footprint (nearest station spawn sits at x ≈ 610).
		float from = RingR + RingW * 0.5f, to = 560f;
		Paint( (from + to) * 0.5f, 0f, to - from, 10f, 0f, "Proving Spur" );
		for ( float x = from + 12f; x < to; x += 20f )
			Dash( x, 0f, alongX: true );

		// marker gantry where the spur meets the hardpack: two posts + a high crossbeam
		Block( new Vector3( 520f, 8f, 3.5f ), new Vector3( 0.8f, 0.8f, 7f ), new Color( 0.85f, 0.65f, 0.10f ), collide: true, name: "Proving Gantry Post" );
		Block( new Vector3( 520f, -8f, 3.5f ), new Vector3( 0.8f, 0.8f, 7f ), new Color( 0.85f, 0.65f, 0.10f ), collide: true, name: "Proving Gantry Post" );
		Block( new Vector3( 520f, 0f, 6.6f ), new Vector3( 0.6f, 16.8f, 0.8f ), new Color( 0.85f, 0.65f, 0.10f ), collide: false, name: "Proving Gantry Beam" );
	}

	// ---------------------------------------------------------------- landmarks (sparse, greybox)

	static void BuildLandmarks()
	{
		// four small destinations in the belt, one per quadrant-ish, matching each city district's
		// character. Deliberately sparse — the belt is a drive, not a second town.
		// NW: farmstead
		if ( !PlaceModel( "models/buildings/barn.vmdl", new Vector2( -150f, 264f ), 90f ) )
			Block( new Vector3( -150f, 264f, 4f ), new Vector3( 14f, 10f, 8f ), new Color( 0.62f, 0.48f, 0.40f ), collide: true, name: "Barn" );
		Block( new Vector3( -138f, 262f, 2.5f ), new Vector3( 4f, 4f, 5f ), new Color( 0.70f, 0.66f, 0.55f ), collide: true, name: "Silo" );

		// NE: depot
		if ( !PlaceModel( "models/buildings/warehouse.vmdl", new Vector2( 150f, 264f ), -90f ) )
			Block( new Vector3( 150f, 264f, 4.5f ), new Vector3( 16f, 12f, 9f ), new Color( 0.55f, 0.55f, 0.52f ), collide: true, name: "Depot" );
		for ( int c = 0; c < 3; c++ )
			Block( new Vector3( 132f + c * 5f, 258f, 1.3f ), new Vector3( 4f, 10f, 2.6f ),
				c == 1 ? new Color( 0.70f, 0.45f, 0.35f ) : new Color( 0.50f, 0.56f, 0.58f ), collide: true, name: "Container" );

		// SW: pond + trees
		var pond = PaintGo( -150f, -264f, 30f, 20f, 0.011f, "Pond" );
		pond.WorldRotation = Rotation.FromYaw( 30f );
		pond.Components.Get<ModelRenderer>().Tint = new Color( 0.42f, 0.58f, 0.72f );
		Tree( new Vector2( -170f, -258f ) );
		Tree( new Vector2( -132f, -272f ) );
		Tree( new Vector2( -160f, -280f ) );

		// SE: scrapyard
		foreach ( var (dx, dy, yaw) in new[] { (-8f, 4f, 15f), (0f, -4f, 80f), (9f, 3f, -30f), (4f, 10f, 55f) } )
		{
			var box = Block( new Vector3( 150f + dx, -264f + dy, 1.3f ), new Vector3( 4f, 10f, 2.6f ),
				new Color( 0.62f, 0.48f, 0.40f ), collide: true, name: "Scrap Container" );
			box.WorldRotation = Rotation.FromYaw( yaw );
		}
	}

	// ---------------------------------------------------------------- return ramps (basin exits)

	static void BuildReturnRamps()
	{
		// gentle wedges from the apron floor back up to the track edges, wide and shallow, their top
		// edges flush with the cliff lines. One per basin side; the basins interconnect.
		ReturnRampX( edgeX: TrackEast, atY: -200f, run: 20f );     // east basin → up the east cliff
		ReturnRampY( edgeY: TrackNorth, atX: 700f, run: 11f );     // north basin → up the north cliff
		ReturnRampY( edgeY: TrackSouth, atX: 700f, run: -20f );    // south basin → up the south cliff
	}

	/// <summary>Wedge in the east basin: top edge on the cliff line x = <paramref name="edgeX"/> at
	/// grade, descending east over <paramref name="run"/> metres to the apron floor.</summary>
	static void ReturnRampX( float edgeX, float atY, float run )
	{
		float drop = -ApronTop;
		float slopeLen = MathF.Sqrt( run * run + drop * drop );
		float pitchDeg = MathF.Atan2( drop, run ).RadianToDegree();   // surface descends toward +X
		const float thick = 0.5f, w = 12f;
		// surface midpoint minus half a thickness along the up-normal
		float nX = MathF.Sin( pitchDeg.DegreeToRadian() ), nZ = MathF.Cos( pitchDeg.DegreeToRadian() );
		var go = Block( new Vector3( edgeX + run * 0.5f - nX * thick * 0.5f, atY, ApronTop * 0.5f - nZ * thick * 0.5f ),
			new Vector3( slopeLen, w, thick ), ApronGrey, collide: true, name: "Return Ramp" );
		go.WorldRotation = Rotation.FromPitch( pitchDeg );
		go.Tags.Add( "road" );
	}

	/// <summary>Wedge in a north/south basin: top edge on the cliff line y = <paramref name="edgeY"/>
	/// at grade, descending away from the track (sign of <paramref name="run"/>) to the floor.</summary>
	static void ReturnRampY( float edgeY, float atX, float run )
	{
		float drop = -ApronTop;
		float slopeLen = MathF.Sqrt( run * run + drop * drop );
		float rollDeg = MathF.Atan2( drop, MathF.Abs( run ) ).RadianToDegree();
		const float thick = 0.5f, w = 12f;
		// surface rises toward the track side; wall-ride-apron convention (yaw 90 + roll, length on
		// local Y → world X). Negative roll rises toward −Y, so flip by which side the track is on.
		float signToTrack = run > 0f ? -1f : 1f;                       // run>0 descends toward +Y
		float nY = MathF.Sign( run ) * MathF.Sin( rollDeg.DegreeToRadian() );
		float nZ = MathF.Cos( rollDeg.DegreeToRadian() );
		var go = Block( new Vector3( atX, edgeY + run * 0.5f - nY * thick * 0.5f, ApronTop * 0.5f - nZ * thick * 0.5f ),
			new Vector3( slopeLen, w, thick ), ApronGrey, collide: true, name: "Return Ramp" );
		go.WorldRotation = Rotation.FromYaw( 90f ) * Rotation.FromRoll( signToTrack * rollDeg );
		go.Tags.Add( "road" );
	}

	// ---------------------------------------------------------------- outer perimeter

	static void BuildOuterPerimeter()
	{
		// Seals the ENTIRE expanded world. Sunk 4 m so the base sits below the apron floors as well
		// as the grade fills; 8 m exposed above grade (11 m above a basin floor — nothing climbs out).
		const float wallH = 8f, sink = 4f, thick = 2f;
		float cz = (wallH - sink) * 0.5f;
		float xMid = (OuterWest + OuterEast) * 0.5f;               // N/S walls span the full width
		float xLen = OuterEast - OuterWest + thick;

		foreach ( var (pos, size, nm) in new (Vector3, Vector3, string)[]
		{
			( new Vector3( xMid, OuterNS, cz ), new Vector3( xLen, thick, wallH + sink ), "Outer Wall N" ),
			( new Vector3( xMid, -OuterNS, cz ), new Vector3( xLen, thick, wallH + sink ), "Outer Wall S" ),
			( new Vector3( OuterWest, 0f, cz ), new Vector3( thick, OuterNS * 2f + thick, wallH + sink ), "Outer Wall W" ),
			( new Vector3( OuterEast, 0f, cz ), new Vector3( thick, OuterNS * 2f + thick, wallH + sink ), "Outer Wall E" ),
		} )
		{
			Block( pos, size, WallGrey, collide: true, name: nm );
			Block( pos.WithZ( wallH + 0.15f ), new Vector3( size.x + 0.5f, size.y + 0.5f, 0.3f ), CapGrey, collide: false, name: nm + " Cap" );
		}
	}

	// ---------------------------------------------------------------- helpers (house primitives)

	/// <summary>Thick grade slab by edge coordinates: top face at <paramref name="top"/>, body
	/// extending <see cref="SlabThick"/> metres down (hard landings cannot tunnel), road-tagged.</summary>
	static void Slab( float x0, float x1, float y0, float y1, float top, Color color, string name )
	{
		var go = Block( new Vector3( (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, top - SlabThick * 0.5f ),
			new Vector3( x1 - x0, y1 - y0, SlabThick ), color, collide: true, name: name );
		go.Tags.Add( "road" );
	}

	/// <summary>Non-colliding road paint just proud of grade.</summary>
	static void Paint( float cx, float cy, float sx, float sy, float extraZ, string name )
		=> PaintGo( cx, cy, sx, sy, 0.01f + extraZ, name );

	static GameObject PaintGo( float cx, float cy, float sx, float sy, float z, string name )
		=> Block( new Vector3( cx, cy, z ), new Vector3( sx, sy, 0.02f ), Asphalt, collide: false, name: name );

	static void Dash( float a, float b, bool alongX )
	{
		var pos = alongX ? new Vector3( a, b, 0.02f ) : new Vector3( b, a, 0.02f );
		var size = alongX ? new Vector3( 2.2f, 0.25f, 0.02f ) : new Vector3( 0.25f, 2.2f, 0.02f );
		Block( pos, size, LaneDash, collide: false, name: "Ring Dash" );
	}

	static void Tree( Vector2 at )
	{
		Block( new Vector3( at.x, at.y, 0.9f ), new Vector3( 0.28f, 0.28f, 1.8f ), new Color( 0.45f, 0.33f, 0.24f ), collide: true, name: "Trunk" );
		var go = Child( "Canopy" );
		go.WorldPosition = new Vector3( at.x, at.y, 2.6f ) * M;
		go.LocalScale = Vector3.One * (2.6f * M / 100f);
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		renderer.Tint = new Color( 0.38f, 0.62f, 0.38f );
	}

	/// <summary>Vendored building model resting on grade with a bounds-fit static collider (the
	/// CityBuilder placement pattern). False when the vmdl is missing so callers box-fallback.</summary>
	static bool PlaceModel( string vmdlPath, Vector2 atMeters, float yaw )
	{
		var model = Model.Load( vmdlPath );
		if ( model is null || model.IsError )
			return false;
		var size = model.Bounds.Size;
		if ( size.z <= 0.01f )
			return false;

		var go = Child( vmdlPath[(vmdlPath.LastIndexOf( '/' ) + 1)..] );
		go.WorldPosition = new Vector3( atMeters.x * M, atMeters.y * M, -model.Bounds.Mins.z );
		go.WorldRotation = Rotation.FromYaw( yaw );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = model.Bounds.Size;
		collider.Center = model.Bounds.Center;
		collider.Static = true;
		return true;
	}

	/// <summary>The one metre→unit conversion site: position and size arrive in SI metres.</summary>
	static GameObject Block( Vector3 posMeters, Vector3 sizeMeters, Color color, bool collide, string name )
	{
		var go = Child( name );
		go.WorldPosition = posMeters * M;
		go.LocalScale = sizeMeters * M / 50f;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = color;

		if ( collide )
		{
			var collider = go.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 50f, 50f, 50f );
			collider.Static = true;
		}
		return go;
	}

	static GameObject Child( string name )
	{
		var go = _scene.CreateObject();
		go.Name = name;
		go.SetParent( _root, true );
		return go;
	}
}
