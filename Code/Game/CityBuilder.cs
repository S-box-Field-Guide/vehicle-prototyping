namespace VehicleProto;

public enum District
{
	Suburbs,    // NW — pastel houses (vendored village building models)
	Downtown,   // NE — commercial core, mini high-rises
	Industrial, // SE — warehouses, ramps, containers
	Park        // SW — green, trees, lake
}

public class CityLayout
{
	public Vector3 SpawnPosition { get; set; }
	public Rotation SpawnFacing { get; set; } = Rotation.Identity;
}

/// <summary>
/// Builds a deterministic 470 m proving-grounds city block at runtime: a 10×10-block
/// grid, four quadrant districts. Fully deterministic (fixed hash of block/lot indices —
/// no runtime RNG). All building ART is custom, flat-shaded, low-poly: the vendored
/// village building set (<c>models/buildings/</c>) for the residential + industrial districts
/// and the generated mini high-rise set (<c>models/city/</c>, see tools/gen_buildings.py)
/// for the downtown commercial core. Colored dev-box fallbacks stand in if a .vmdl is
/// missing, so the map is always drivable. The drivable area is fully enclosed by solid
/// perimeter walls (no car can leave the world at any speed it reaches).
/// </summary>
public static class CityBuilder
{
	const float M = Units.MetersToUnits;

	public const int GridBlocks = 10;
	public const float BlockSize = 36f;  // m
	public const float RoadWidth = 10f;  // m
	public const float Cell = BlockSize + RoadWidth;
	public const float Total = GridBlocks * Cell + RoadWidth; // 470 m
	public const float Origin = -Total * 0.5f;                // map centered on world origin

	static readonly Color Asphalt = new( 0.30f, 0.30f, 0.33f );
	static readonly Color ParkPath = new( 0.72f, 0.66f, 0.52f );
	static readonly Color Sidewalk = new( 0.62f, 0.62f, 0.64f );
	static readonly Color Grass = new( 0.55f, 0.72f, 0.45f );

	static readonly Color[] SuburbPalette =
	{
		new( 0.93f, 0.87f, 0.78f ), new( 0.85f, 0.62f, 0.52f ), new( 0.72f, 0.80f, 0.68f ),
		new( 0.80f, 0.76f, 0.85f ), new( 0.95f, 0.80f, 0.62f ),
	};

	static readonly Color[] DowntownPalette =
	{
		new( 0.55f, 0.65f, 0.75f ), new( 0.75f, 0.72f, 0.68f ), new( 0.45f, 0.52f, 0.62f ),
		new( 0.82f, 0.78f, 0.72f ), new( 0.60f, 0.70f, 0.72f ),
	};

	static readonly Color[] IndustrialPalette =
	{
		new( 0.62f, 0.48f, 0.40f ), new( 0.55f, 0.55f, 0.52f ), new( 0.70f, 0.45f, 0.35f ),
		new( 0.50f, 0.56f, 0.58f ),
	};

	// Residential / village models — vendored village set (retires the Kenney suburban kit).
	// Placed at NATURAL scale (they are authored at real metres); box fallback if missing.
	static readonly string[] SuburbModels =
	{
		"models/buildings/house_small.vmdl",
		"models/buildings/house_medium.vmdl",
		"models/buildings/house_large.vmdl",
		"models/buildings/inn.vmdl",
		"models/buildings/shop.vmdl",
		"models/buildings/civic_hall.vmdl",
	};

	// Downtown commercial core — generated art (tools/gen_buildings.py), placed at natural
	// scale. Each block is PACKED with a couple of structures (pre-launch note: not one
	// lonely tower per block) — one tall anchor tower + 1-3 smaller commercial fill buildings,
	// arranged into block quadrants by deterministic hash. Box fallbacks if a vmdl is missing.
	//
	// Tower anchors: the pack subset that fits a quadrant with a comfortable gap (highrise_d
	// at 18 m wide is too broad for packing, so it is left out of downtown).
	static readonly string[] DowntownTowers =
	{
		"models/city/highrise_a.vmdl",
		"models/city/highrise_b.vmdl",
		"models/city/highrise_c.vmdl",
		"models/city/highrise_e.vmdl",
	};

	// Small commercial fill (1-3 storeys) — storefronts/offices that sit beside the towers.
	static readonly string[] CommercialModels =
	{
		"models/city/shop_a.vmdl",
		"models/city/shop_b.vmdl",
		"models/city/shop_c.vmdl",
		"models/city/shop_d.vmdl",
	};

	// Industrial district — vendored work buildings (natural scale).
	static readonly string[] IndustrialModels =
	{
		"models/buildings/warehouse.vmdl",
		"models/buildings/workshop.vmdl",
		"models/buildings/barn.vmdl",
	};

	static Scene _scene;
	static GameObject _cityRoot;

	public static CityLayout Build( Scene scene, int seed = 1971 )
	{
		_scene = scene;
		_cityRoot = scene.CreateObject();
		_cityRoot.Name = "City";

		var layout = new CityLayout();

		BuildGround();
		BuildRoads();
		BuildPerimeterWall();

		for ( int bx = 0; bx < GridBlocks; bx++ )
		for ( int by = 0; by < GridBlocks; by++ )
		{
			var district = DistrictOf( bx, by );
			var blockMin = new Vector2( Origin + RoadWidth + bx * Cell, Origin + RoadWidth + by * Cell );

			switch ( district )
			{
				case District.Suburbs: BuildSuburbBlock( blockMin, bx, by ); break;
				case District.Downtown: BuildDowntownBlock( blockMin, bx, by ); break;
				case District.Industrial: BuildIndustrialBlock( blockMin, bx, by ); break;
				case District.Park: BuildParkBlock( blockMin, bx, by ); break;
			}
		}

		// street dressing along the two main avenues (pre-launch "spruce-ups")
		BuildCrosswalks();
		BuildStreetlights();

		// spawn: the central intersection, facing east down the main avenue
		layout.SpawnPosition = new Vector3( 0f, 0f, 0f );
		layout.SpawnFacing = Rotation.Identity;

		Log.Info( $"[vp] city built: {GridBlocks}x{GridBlocks} blocks, {Total:F0}m span" );
		return layout;
	}

	static District DistrictOf( int bx, int by )
	{
		bool west = bx < GridBlocks / 2;
		bool north = by >= GridBlocks / 2;
		return north
			? west ? District.Suburbs : District.Downtown
			: west ? District.Park : District.Industrial;
	}

	// Deterministic per-block hash (FNV-1a + an avalanche finalizer). No System.Random —
	// the whole city (and, in the wider stack, the networking contract) requires that the
	// same block coords always produce the same layout. `salt` mints an independent stream
	// per decision (which quadrant is the tower, fill count, model pick, yaw), so no two
	// derived values correlate. Returns a well-mixed uint the callers reduce with % / bits.
	static uint Hash( int a, int b, uint salt )
	{
		unchecked
		{
			uint h = 2166136261u;
			h = (h ^ (uint)a) * 16777619u;
			h = (h ^ (uint)b) * 16777619u;
			h = (h ^ salt) * 16777619u;
			h ^= h >> 15;
			h *= 0x2c1b3c6du;
			h ^= h >> 12;
			return h;
		}
	}

	// ---------------------------------------------------------------- terrain & roads

	static void BuildGround()
	{
		// one thick slab under everything — hard landings tunnel through thin colliders
		var go = Child( "Ground" );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/plane.vmdl" );
		renderer.Tint = Grass;
		go.LocalScale = new Vector3( Total * 1.2f * M / 100f, Total * 1.2f * M / 100f, 1f );

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 100f, 100f, 200f );
		collider.Center = Vector3.Down * 100f;
		collider.Static = true;

		go.Tags.Add( "road" );
	}

	static void BuildRoads()
	{
		// full-length strips both ways; park quadrant gets path-colored overlay strips
		for ( int i = 0; i <= GridBlocks; i++ )
		{
			float offset = Origin + i * Cell + RoadWidth * 0.5f;
			Block( new Vector3( offset, 0f, 0.01f ) * M, new Vector3( RoadWidth, Total, 0.02f ), Asphalt, collide: false, name: "Road NS" );
			Block( new Vector3( 0f, offset, 0.01f ) * M, new Vector3( Total, RoadWidth, 0.02f ), Asphalt, collide: false, name: "Road EW" );
		}

		// dashed center line on the two main avenues through the origin. Dashes that would fall
		// under a crosswalk band are skipped, so a lane line terminates before each crossing instead
		// of overprinting the zebra stripes.
		for ( int i = 0; i < 46; i++ )
		{
			float t = Origin + 4f + i * (Total / 46f);
			if ( NearCrosswalkBand( t ) )
				continue;
			Block( new Vector3( t, 0f, 0.02f ) * M, new Vector3( 2.2f, 0.25f, 0.02f ), new Color( 0.9f, 0.85f, 0.55f ), collide: false, name: "Lane" );
			Block( new Vector3( 0f, t, 0.02f ) * M, new Vector3( 0.25f, 2.2f, 0.02f ), new Color( 0.9f, 0.85f, 0.55f ), collide: false, name: "Lane" );
		}
	}

	// Solid perimeter wall enclosing the whole drivable city (the world
	// must NOT be drivable-off). 8 m tall, 2 m thick, static colliders — well beyond any
	// jump the cars can make, so no vehicle leaves the map at any speed it reaches. Sits
	// just outside the outermost ring road; deterministic (crash-wall collider precedent).
	// WORLD PASS (2026-07-19): each side now carries a GATE — a gap centred on the main avenue
	// (the two avenues run through the origin, so the gap sits at the middle of every side) —
	// opening the city to the Outskirts belt (ring road, proving-grounds connector). The world
	// stays sealed one layer out by the Outskirts outer perimeter. Gate posts flank each gap so
	// the openings read from a distance.
	const float WallHeight = 8f;   // m
	const float WallThick = 2f;    // m
	const float GateWidth = 12f;   // m opening centred on each avenue

	/// <summary>The city wall line's distance from origin (also the Outskirts belt's inner
	/// boundary — the belt builder reads this so the two stay in lockstep).</summary>
	public const float WallEdge = -Origin + RoadWidth * 0.5f + 2f;

	static void BuildPerimeterWall()
	{
		float edge = WallEdge;
		var wall = new Color( 0.52f, 0.52f, 0.55f );
		var cap = new Color( 0.40f, 0.40f, 0.44f );    // contrasting coping so the wall reads
		var post = new Color( 0.62f, 0.55f, 0.30f );   // gate posts, warm so the exits read

		// Each side is TWO segments leaving a GateWidth gap at the avenue (side centre). Segments
		// run from the gap edge to half a wall-thickness beyond ±edge, so the four sides still
		// OVERLAP at the corners — no open corner notch (the full-span lesson, kept).
		float cornerReach = edge + WallThick;                   // segment outer end (overlaps corner)
		float segLen = cornerReach - GateWidth * 0.5f;          // one segment's length
		float segMid = GateWidth * 0.5f + segLen * 0.5f;        // its centre along the wall axis

		foreach ( var (axisX, sign, nm) in new[]
		{
			( true, 1f, "Perimeter Wall N" ),    // wall along X at y = +edge
			( true, -1f, "Perimeter Wall S" ),
			( false, 1f, "Perimeter Wall E" ),   // wall along Y at x = +edge
			( false, -1f, "Perimeter Wall W" ),
		} )
		{
			foreach ( float segSign in new[] { 1f, -1f } )
			{
				var pos = axisX
					? new Vector3( segSign * segMid, sign * edge, WallHeight * 0.5f )
					: new Vector3( sign * edge, segSign * segMid, WallHeight * 0.5f );
				var size = axisX
					? new Vector3( segLen, WallThick, WallHeight )
					: new Vector3( WallThick, segLen, WallHeight );
				var capSize = axisX
					? new Vector3( segLen, WallThick + 0.5f, 0.3f )
					: new Vector3( WallThick + 0.5f, segLen, 0.3f );
				Block( pos * M, size, wall, collide: true, name: nm );
				Block( pos.WithZ( WallHeight + 0.15f ) * M, capSize, cap, collide: false, name: nm + " Cap" );
			}

			// gate posts at the two gap edges, just proud of the wall line
			foreach ( float postSign in new[] { 1f, -1f } )
			{
				float along = postSign * (GateWidth * 0.5f + 0.6f);
				var postPos = axisX
					? new Vector3( along, sign * edge, 4.75f )
					: new Vector3( sign * edge, along, 4.75f );
				Block( postPos * M, new Vector3( 1.2f, 1.2f, 9.5f ), post, collide: true, name: nm + " Gate Post" );
			}
		}
	}

	// ---------------------------------------------------------------- district blocks

	static void BuildSuburbBlock( Vector2 min, int bx, int by )
	{
		SidewalkApron( min );

		// 2×2 house lots; vendored village models at natural scale, box fallback.
		for ( int lot = 0; lot < 4; lot++ )
		{
			float lotX = min.x + (lot % 2 == 0 ? 9.5f : BlockSize - 9.5f);
			float lotY = min.y + (lot < 2 ? 9.5f : BlockSize - 9.5f);
			int seed = bx * 7 + by * 13 + lot * 29;

			string model = SuburbModels[seed % SuburbModels.Length];
			float yaw = lot % 2 == 0 ? 90f : -90f; // face the nearest N-S street
			if ( !PlaceModel( model, new Vector2( lotX, lotY ), yaw, targetHeight: 0f ) )
			{
				Block( new Vector3( lotX, lotY, 3f ) * M, new Vector3( 10f, 9f, 6f ),
					SuburbPalette[seed % SuburbPalette.Length] );
			}
		}

		// a lawn tree on one corner
		if ( (bx + by) % 2 == 0 )
			Tree( min + new Vector2( BlockSize - 5f, 5f ) );
	}

	// Quadrant offset of the four building pads inside a block (block-local metres from the
	// block centre). qc keeps even the widest packed footprint (~7 m half-extent) inside the
	// block with a sidewalk margin, and adjacent pads 2*qc apart leave a 3-5 m gap between
	// buildings (no touching, no z-fight).
	const float QuadOffset = 8.5f;
	static readonly Vector2[] Quadrants =
	{
		new( -QuadOffset, -QuadOffset ), new( QuadOffset, -QuadOffset ),
		new( -QuadOffset, QuadOffset ),  new( QuadOffset, QuadOffset ),
	};

	static void BuildDowntownBlock( Vector2 min, int bx, int by )
	{
		SidewalkApron( min );

		// Pack the block: one tall anchor tower + 1-3 smaller commercial buildings, one per
		// quadrant, so a block reads as a cluster, not a lonely centred tower.
		// Everything is deterministic hash-of-block-coords, so adjacent blocks differ (tower
		// position, fill count, models, yaws) but a regen is identical.
		var centre = new Vector2( min.x + BlockSize * 0.5f, min.y + BlockSize * 0.5f );
		int towerQuad = (int)(Hash( bx, by, 0x01 ) % 4);
		int fillCount = 1 + (int)(Hash( bx, by, 0x02 ) % 3);      // 1-3 shops -> 2-4 structures
		int startQuad = (int)(Hash( bx, by, 0x03 ) % 4);          // rotate fill order so empties vary

		int filled = 0;
		for ( int i = 0; i < 4; i++ )
		{
			int q = (startQuad + i) % 4;
			var at = centre + Quadrants[q];

			if ( q == towerQuad )
			{
				PlaceTower( at, Hash( bx, by, 0x10u + (uint)q ) );
			}
			else if ( filled < fillCount )
			{
				filled++;
				PlaceShop( at, Hash( bx, by, 0x20u + (uint)q ) );
			}
			else
			{
				// empty quadrant — dress it as a small painted parking lot
				BuildParkingBay( at, Hash( bx, by, 0x30u + (uint)q ) );
			}
		}

		// two planters at opposite sidewalk mid-edges (clear of the quadrant pads), so a
		// downtown block has a little street furniture even when sparsely built
		uint hp = Hash( bx, by, 0x40 );
		Vector2[] edges = { new( 0f, 14f ), new( 0f, -14f ), new( 14f, 0f ), new( -14f, 0f ) };
		int e0 = (int)(hp & 3);
		int e1 = (e0 + 1 + (int)((hp >> 4) % 3)) & 3; // offset 1-3 -> always a different edge
		Planter( centre + edges[e0] );
		Planter( centre + edges[e1] );
	}

	static void PlaceTower( Vector2 at, uint h )
	{
		string model = DowntownTowers[h % (uint)DowntownTowers.Length];
		float yaw = 90f * ((h >> 3) & 3);
		if ( PlaceModel( model, at, yaw, targetHeight: 0f ) )
			return;
		float ht = 18f + (h % 4) * 4f; // 18-30 m box-tower fallback
		var c = DowntownPalette[h % (uint)DowntownPalette.Length];
		Block( new Vector3( at.x, at.y, ht * 0.5f ) * M, new Vector3( 12f, 12f, ht ), c, name: "Tower" );
		Block( new Vector3( at.x, at.y, ht + 0.4f ) * M, new Vector3( 9f, 9f, 0.8f ), c.Darken( 0.3f ) );
	}

	static void PlaceShop( Vector2 at, uint h )
	{
		string model = CommercialModels[h % (uint)CommercialModels.Length];
		float yaw = 90f * ((h >> 3) & 3);
		if ( PlaceModel( model, at, yaw, targetHeight: 0f ) )
			return;
		float ht = 3.5f + (h % 3) * 3.3f; // ~3.5-10 m box-shop fallback
		var c = DowntownPalette[(h >> 2) % (uint)DowntownPalette.Length];
		Block( new Vector3( at.x, at.y, ht * 0.5f ) * M, new Vector3( 10f, 9f, ht ), c, name: "Shop" );
		Block( new Vector3( at.x, at.y, ht + 0.3f ) * M, new Vector3( 8f, 7f, 0.6f ), c.Darken( 0.25f ) );
	}

	static void BuildIndustrialBlock( Vector2 min, int bx, int by )
	{
		SidewalkApron( min );
		int seed = bx * 5 + by * 19;

		if ( seed % 4 == 0 )
		{
			// open container yard with a ramp — the district's "jump here" invitation
			Ramp( min + new Vector2( BlockSize * 0.5f, BlockSize * 0.35f ), 14f );
			for ( int c = 0; c < 3; c++ )
			{
				var color = IndustrialPalette[(seed + c) % IndustrialPalette.Length];
				Block( new Vector3( min.x + 6f + c * 4.5f, min.y + BlockSize - 8f, 1.3f ) * M,
					new Vector3( 4f, 10f, 2.6f ), color, name: "Container" );
			}
		}
		else
		{
			// one work building (natural scale), box-warehouse fallback
			var centre = new Vector2( min.x + BlockSize * 0.5f, min.y + BlockSize * 0.5f );
			string model = IndustrialModels[seed % IndustrialModels.Length];
			float yaw = 90f * (seed % 4);
			if ( !PlaceModel( model, centre, yaw, targetHeight: 0f ) )
			{
				var color = IndustrialPalette[seed % IndustrialPalette.Length];
				float h = 8f + seed % 3 * 2f;
				Block( new Vector3( centre.x, centre.y, h * 0.5f ) * M,
					new Vector3( BlockSize - 10f, BlockSize - 14f, h ), color, name: "Warehouse" );
				Block( new Vector3( centre.x, centre.y, h + 0.6f ) * M,
					new Vector3( BlockSize - 16f, BlockSize - 20f, 1.2f ), color.Darken( 0.25f ) );
			}
		}
	}

	static void BuildParkBlock( Vector2 min, int bx, int by )
	{
		// no sidewalk — grass runs to the road; scattered trees + a diagonal path
		int seed = bx * 3 + by * 7;
		for ( int t = 0; t < 4; t++ )
		{
			float tx = min.x + 5f + (seed * 31 + t * 41) % (int)(BlockSize - 10f);
			float ty = min.y + 5f + (seed * 17 + t * 53) % (int)(BlockSize - 10f);
			Tree( new Vector2( tx, ty ) );
		}

		// drivable diagonal path. Length is derived so the 45°-rotated strip — INCLUDING its width —
		// stays a curb margin inside the block, so its pointed corners terminate cleanly on the grass
		// instead of poking over the road edge. (Was a fixed BlockSize·1.3, whose corners overran the
		// curb.) A 45° rectangle reaches (halfLen + halfWidth)/√2 from centre along each axis; keep
		// that within BlockSize/2 − curbMargin.
		if ( (bx + by) % 2 == 1 )
		{
			const float pathWidth = 4.5f, curbMargin = 1f;
			float pathLen = ((BlockSize * 0.5f - curbMargin) * MathF.Sqrt( 2f ) - pathWidth * 0.5f) * 2f;
			var go = Block( new Vector3( min.x + BlockSize * 0.5f, min.y + BlockSize * 0.5f, 0.015f ) * M,
				new Vector3( pathLen, pathWidth, 0.02f ), ParkPath, collide: false, name: "Path" );
			go.WorldRotation = Rotation.FromYaw( 45f );
		}
	}

	// ---------------------------------------------------------------- pieces

	// Raised kerbed sidewalk (pre-launch note: "sidewalks with a subtle height
	// difference so people can get a sense for driving on different things"). The whole
	// block pad sits KerbHeight proud of the road with a STATIC collider, so driving off
	// a road onto any block gives a real-but-gentle kerb bump at any speed. Sharp box
	// steps are harder to climb than the height alone suggests (raycast wheels hit the
	// vertical face) — 0.12 m hung every class in free drive (2026-07-14 telemetry:
	// Sidewalk Fz spikes + miss wheels at ~(88,99)/(144,94); kart travel was only
	// 0.12 m and hatch GroundClearance 0.12 m). 0.06 m stays visibly proud, under
	// kart clearance, and well under street-car travel (roster +2 cm clearance pass).
	// The pad is thick (extends DOWN into the ground slab) so hard landings can't tunnel
	// it; only the top KerbHeight shows above the road. Buildings rest at ground z=0 and
	// visually emerge THROUGH the pad top (their sunk base is occluded by the pad), so no
	// lift/z-fight bookkeeping is needed. Park blocks skip this (grass runs to the road).
	const float KerbHeight = 0.06f; // m proud of the road (was 0.12 — hung cars)
	const float PadThick = 0.5f;    // m collider depth; only the top KerbHeight is visible

	static void SidewalkApron( Vector2 min )
	{
		float cz = KerbHeight - PadThick * 0.5f; // centre so the TOP lands at KerbHeight
		Block( new Vector3( min.x + BlockSize * 0.5f, min.y + BlockSize * 0.5f, cz ) * M,
			new Vector3( BlockSize, BlockSize, PadThick ), Sidewalk, collide: true, name: "Sidewalk" );
	}

	// ---------------------------------------------------------------- street dressing
	// Cheap, deterministic, in-style details that earn their keep at driving speed. All are
	// flat paint boxes (collide:false, just proud of their surface so no z-fight) EXCEPT the
	// streetlight poles and planters, which carry colliders so hitting one is a real event.

	static readonly Color CrosswalkPaint = new( 0.88f, 0.88f, 0.84f );

	const float CrosswalkDepth = 3f;    // along-travel depth of a crossing band (m)
	const float LaneDashHalfLen = 1.1f; // half the length of a centre-line dash (m)
	// A crossing sits at the MOUTH of a junction — its stop line, pulled clear of the junction box —
	// not dead-centre in it. Offset from the cross-road centreline by half the road width plus half
	// the band depth, so the band's inner edge lands right at the junction edge.
	const float CrosswalkApproachOffset = RoadWidth * 0.5f + CrosswalkDepth * 0.5f; // 6.5 m

	// Zebra crossings on the two main avenues (through the origin). At each cross-street the crossing
	// is painted on BOTH approaches (the two stop lines framing the junction) instead of a single band
	// dumped in the junction centre — so the stripes sit at the intersection edges, clear of the
	// cross-traffic box and of the other avenue's crossing. The player is on these avenues most, so
	// the paint reads constantly.
	static void BuildCrosswalks()
	{
		float limit = Total * 0.5f - CrosswalkDepth * 0.5f; // keep bands on the paved avenue
		for ( int i = 0; i <= GridBlocks; i++ )
		{
			float cross = Origin + i * Cell + RoadWidth * 0.5f; // a cross-road centreline
			foreach ( float c in new[] { cross - CrosswalkApproachOffset, cross + CrosswalkApproachOffset } )
			{
				if ( MathF.Abs( c ) > limit )
					continue;
				Zebra( new Vector2( 0f, c ), acrossX: true );   // across the NS avenue (x=0)
				Zebra( new Vector2( c, 0f ), acrossX: false );  // across the EW avenue (y=0)
			}
		}
	}

	// True if avenue coordinate t lies under (or right up against) a crosswalk band — used to gap the
	// centre-line dashes so a lane line stops at a crossing rather than overprinting it. Symmetric in
	// the two avenues, so one test serves both.
	static bool NearCrosswalkBand( float t )
	{
		const float clear = CrosswalkDepth * 0.5f + LaneDashHalfLen;
		for ( int i = 0; i <= GridBlocks; i++ )
		{
			float cross = Origin + i * Cell + RoadWidth * 0.5f;
			if ( MathF.Abs( t - (cross - CrosswalkApproachOffset) ) < clear ) return true;
			if ( MathF.Abs( t - (cross + CrosswalkApproachOffset) ) < clear ) return true;
		}
		return false;
	}

	// Five stripes over a ~3 m-deep crossing spanning most of the 10 m road width. acrossX =
	// stripes run along X (the crossing spans the NS avenue); else along Y.
	static void Zebra( Vector2 at, bool acrossX )
	{
		const int n = 5;
		const float span = 8f, depth = CrosswalkDepth, sw = depth / n * 0.55f; // stripe width
		for ( int s = 0; s < n; s++ )
		{
			float t = -depth * 0.5f + (s + 0.5f) / n * depth;
			var pos = acrossX ? new Vector3( at.x, at.y + t, 0.03f ) : new Vector3( at.x + t, at.y, 0.03f );
			var size = acrossX ? new Vector3( span, sw, 0.02f ) : new Vector3( sw, span, 0.02f );
			Block( pos * M, size, CrosswalkPaint, collide: false, name: "Crosswalk" );
		}
	}

	// Streetlight poles down both sides of each main avenue, one per block. The pole carries
	// a collider ("hitting a pole should be a thing"); the arm + lamp head reach
	// out over the road well above car height and are visual-only.
	static void BuildStreetlights()
	{
		// Poles sit 3 m back from the road edge (was 1 m). At 1 m the poles stood right at the edge of
		// the 10 m roadway, so a wide vehicle using the full lane clipped them at speed like invisible
		// walls; 3 m clears the widest car with margin even when it hugs the curb. The arm reaches back
		// over the roadway so the lamp head still overhangs the lane.
		float edge = RoadWidth * 0.5f + 3f;
		float arm = (edge - RoadWidth * 0.5f) + 1.4f; // lamp head hangs ~1.4 m inside the road edge, over the lane
		for ( int k = 0; k < GridBlocks; k++ )
		{
			float c = Origin + RoadWidth + k * Cell + BlockSize * 0.5f; // ~each block centre
			Streetlight( new Vector2( edge, c ), new Vector2( -arm, 0f ) );  // NS avenue, east side
			Streetlight( new Vector2( -edge, c ), new Vector2( arm, 0f ) );  // NS avenue, west side
			Streetlight( new Vector2( c, edge ), new Vector2( 0f, -arm ) );  // EW avenue, north side
			Streetlight( new Vector2( c, -edge ), new Vector2( 0f, arm ) );  // EW avenue, south side
		}
	}

	static void Streetlight( Vector2 at, Vector2 arm )
	{
		var poleColor = new Color( 0.24f, 0.25f, 0.27f );
		const float poleH = 6f;
		Block( new Vector3( at.x, at.y, poleH * 0.5f ) * M, new Vector3( 0.18f, 0.18f, poleH ),
			poleColor, collide: true, name: "Streetlight" );

		bool ax = MathF.Abs( arm.x ) > MathF.Abs( arm.y );
		float armLen = ax ? MathF.Abs( arm.x ) : MathF.Abs( arm.y );
		var mid = at + arm * 0.5f;
		var head = at + arm;
		var armSize = ax ? new Vector3( armLen, 0.14f, 0.14f ) : new Vector3( 0.14f, armLen, 0.14f );
		Block( new Vector3( mid.x, mid.y, poleH - 0.2f ) * M, armSize, poleColor, collide: false, name: "Lamp Arm" );
		Block( new Vector3( head.x, head.y, poleH - 0.35f ) * M, new Vector3( 0.7f, 0.4f, 0.25f ),
			new Color( 0.96f, 0.92f, 0.72f ), collide: false, name: "Lamp" );
	}

	// A small concrete planter with a shrub, on a downtown sidewalk. Collidable street
	// furniture — solid, so bumping one on the pavement is a real (small) obstacle.
	static void Planter( Vector2 at )
	{
		Block( new Vector3( at.x, at.y, KerbHeight + 0.25f ) * M, new Vector3( 1.6f, 1.6f, 0.5f ),
			new Color( 0.55f, 0.55f, 0.58f ), collide: true, name: "Planter" );
		Sphere( new Vector3( at.x, at.y, KerbHeight + 0.5f + 0.9f ), 1.9f, new Color( 0.38f, 0.6f, 0.38f ), collide: false );
	}

	// An empty downtown quadrant, dressed as a small painted parking lot: a dark tarmac
	// patch on the raised sidewalk with a few stall lines. Flat, non-collidable (drive over
	// it), sitting just proud of the kerb top so it reads without z-fighting the pad.
	static void BuildParkingBay( Vector2 at, uint h )
	{
		Block( new Vector3( at.x, at.y, KerbHeight + 0.004f ) * M, new Vector3( 11f, 11f, 0.01f ),
			new Color( 0.27f, 0.27f, 0.29f ), collide: false, name: "Parking Lot" );
		bool alongX = (h & 1) == 0; // stall orientation varies per bay
		var line = new Color( 0.82f, 0.82f, 0.78f );
		for ( int s = 0; s <= 3; s++ )
		{
			float t = -4.5f + s * 3f;
			var pos = alongX ? new Vector3( at.x + t, at.y, KerbHeight + 0.007f )
							 : new Vector3( at.x, at.y + t, KerbHeight + 0.007f );
			var size = alongX ? new Vector3( 0.15f, 9f, 0.01f ) : new Vector3( 9f, 0.15f, 0.01f );
			Block( pos * M, size, line, collide: false, name: "Bay Line" );
		}
	}

	static void Tree( Vector2 at )
	{
		Block( new Vector3( at.x, at.y, 0.9f ) * M, new Vector3( 0.28f, 0.28f, 1.8f ), new Color( 0.45f, 0.33f, 0.24f ), name: "Trunk" );
		Sphere( new Vector3( at.x, at.y, 2.6f ), 2.6f, new Color( 0.38f, 0.62f, 0.38f ), collide: false );
	}

	static void Ramp( Vector2 at, float pitchDegrees )
	{
		var size = new Vector3( 12f, 6f, 0.4f );
		var go = Child( "Ramp" );
		go.WorldPosition = new Vector3( at.x, at.y, MathF.Sin( pitchDegrees.DegreeToRadian() ) * size.x * 0.25f ) * M;
		go.WorldRotation = Rotation.FromPitch( -pitchDegrees );
		go.LocalScale = size * M / 50f;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = new Color( 0.55f, 0.55f, 0.6f );

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 50f, 50f, 50f );
		collider.Static = true;
	}

	static void Sphere( Vector3 positionMeters, float diameterMeters, Color color, bool collide = false )
	{
		var go = Child( "Sphere" );
		go.WorldPosition = positionMeters * M;
		go.LocalScale = Vector3.One * (diameterMeters * M / 100f); // dev sphere ≈ 100 u diameter

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		renderer.Tint = color;

		if ( collide )
		{
			var collider = go.Components.Create<SphereCollider>();
			collider.Radius = 50f;
			collider.Static = true;
		}
	}

	/// <summary>
	/// Place a real model resting on the ground, with a bounds-fit static box collider
	/// (collider dims are LOCAL — GO scale does the sizing). <paramref name="targetHeight"/>
	/// &gt; 0 auto-scales the model to that height in metres; &lt;= 0 uses the model's NATURAL
	/// scale (the building/high-rise vmdls are authored at real metres). Returns false if the vmdl
	/// isn't available so callers can fall back to boxes.
	/// </summary>
	static bool PlaceModel( string vmdlPath, Vector2 atMeters, float yaw, float targetHeight )
	{
		var model = Model.Load( vmdlPath );
		if ( model is null || model.IsError )
			return false;

		var size = model.Bounds.Size;
		if ( size.z <= 0.01f )
			return false;

		float scale = targetHeight > 0f ? targetHeight * M / size.z : 1f;

		var go = Child( vmdlPath[(vmdlPath.LastIndexOf( '/' ) + 1)..] );
		go.WorldPosition = new Vector3( atMeters.x * M, atMeters.y * M, -model.Bounds.Mins.z * scale );
		go.WorldRotation = Rotation.FromYaw( yaw );
		go.LocalScale = Vector3.One * scale;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = model.Bounds.Size;
		collider.Center = model.Bounds.Center;
		collider.Static = true;

		return true;
	}

	static GameObject Block( Vector3 position, Vector3 sizeMeters, Color color, bool collide = true, string name = "Block" )
	{
		var go = Child( name );
		go.WorldPosition = position;
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
		go.SetParent( _cityRoot, true );
		return go;
	}
}
