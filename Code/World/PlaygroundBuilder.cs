namespace VehicleProto;

/// <summary>Layout result for the playground world — where the default car spawns.</summary>
public sealed class PlaygroundLayout
{
	public Vector3 SpawnPosition { get; set; }
	public Rotation SpawnFacing { get; set; } = Rotation.Identity;
}

/// <summary>
/// The PLAYGROUND world: a pure STUNT PARK (build 9 redesign, pass 2). No town, no buildings, no city
/// road network — just a big flat playfield MAXED OUT with jumps of every size (small pops up to
/// huge-air), chained rhythm lines, side-by-side opposed gap jumps, a big-air launch, a jump-onto-box,
/// a ball pit, and a few blockout stunt extras, so it reads as a distinct map from the proving-grounds
/// measurement scene. Kept from the old layout: the flat ground core + outer rolling-hills terrain
/// (<see cref="PlaygroundTerrain"/>), the banked bowl, and the sealing perimeter wall.
///
/// Every solid feature follows the standing DRIVABILITY LAW: entry tangent to grade (no wheel-catching
/// lip), closed underside (no drive-under trap), collision that follows the actual curved shape (the
/// <see cref="RampKicker"/> segmented tangent-box collider). Curved kickers come from RampKicker.
///
/// ORIENTATION LAW (pass 2): jumps along one DRIVE LINE all face the SAME way (a rhythm run you link);
/// jumps that sit SIDE BY SIDE (adjacent, not in-line) face OPPOSITE ways — an opposed gap pair whose
/// two lips point at each other — so from any approach heading a player meets a launch FACE, never just
/// a wedge back. Directional set-pieces (the height ladder, the big-air) are the deliberate exception:
/// each has its own one-way runway. Deterministic: fixed layout + a fixed ball table, no RNG.
/// </summary>
public static class PlaygroundBuilder
{
	const float M = Units.MetersToUnits;

	// palette (plain greybox stunt-park)
	static readonly Color GroundTan = new( 0.62f, 0.58f, 0.48f );
	static readonly Color RampGrey = new( 0.52f, 0.52f, 0.58f );
	static readonly Color BoxGrey = new( 0.55f, 0.55f, 0.60f );
	static readonly Color BowlGrey = new( 0.46f, 0.48f, 0.54f );
	static readonly Color RunwayGrey = new( 0.30f, 0.30f, 0.33f );

	// The five showcase kicker heights (metres). Distinct, clearly-different pops spread small→huge;
	// colour-coded green→red so a player reads the height at a glance, with a matching marker pylon
	// beside each ladder kicker. The tall end is the ~2–3× scale-up (pass 2): 4.5 m is a big-air pop.
	static readonly float[] LadderHeights = { 0.6f, 1.2f, 2.0f, 3.0f, 4.5f };

	public const float GroundHalf = 260f;   // m — flat pad half-extent (terrain flat core ±172 m sits inside)

	static Scene _scene;
	static GameObject _root;
	static bool _terrain;

	// running census (reported at the end)
	static int _ramps, _bowlSegs, _balls, _boxes;

	/// <summary>Build the playground. <paramref name="terrain"/> = W4 Option B: replace the flat pad
	/// with a gentle heightfield (structures still sit on its flat core). Default false = Option A
	/// (flat plane), the accepted fallback and the safe raycast-wheel baseline.</summary>
	public static PlaygroundLayout Build( Scene scene, bool terrain = false )
	{
		_scene = scene;
		_root = scene.CreateObject();
		_root.Name = "Playground";
		_terrain = terrain;
		_ramps = _bowlSegs = _balls = _boxes = 0;

		BuildGround();

		// ---- runup guides (flush, non-colliding dark strips that read as "build speed here") ----
		Runway( new Vector2( -150f, 0f ), new Vector2( -40f, 0f ), 16f );     // spawn → kicker ladder
		Runway( new Vector2( -150f, 90f ), new Vector2( -30f, 90f ), 12f );   // west feed → rhythm field
		Runway( new Vector2( 5f, -130f ), new Vector2( 95f, -130f ), 14f );   // big-air runway (south-east)

		BuildKickerLadder( new Vector2( -60f, 0f ) );   // the FIVE showcase heights, side by side (one-way)
		BuildRhythmLines();                             // chained same-direction kicker lines you rhythm
		BuildBigAir( new Vector2( 95f, -130f ) );       // huge 6 m launch + landing ramp down the runway
		BuildOpposedPairs();                            // side-by-side opposed gap jumps (bidirectional)
		BuildScatterSingles();                          // more coverage singles across the open field
		BuildJumpOntoBox( new Vector2( 40f, -25f ) );   // launch → land on an elevated box → ramp down
		BuildBallPit( new Vector2( 110f, 85f ) );
		BuildStuntExtras();                             // tabletop, step-stairs, wall-ride

		BuildBankedBowl( new Vector2( -125f, -125f ), radiusM: 34f, bankDeg: 26f );  // SW corner, clear of the ladder lanes
		BuildPerimeterWall();

		var layout = new PlaygroundLayout
		{
			// open west apron, facing east straight down the ladder runway
			SpawnPosition = new Vector3( -150f, 0f, 0f ),
			SpawnFacing = Rotation.Identity,
		};

		Log.Info( $"[vp] playground (stunt park) built: {_ramps} kickers, {_boxes} boxes/platforms, " +
			$"banked bowl {_bowlSegs} segs, {_balls} balls" );
		return layout;
	}

	// ---------------------------------------------------------------- ground

	static void BuildGround()
	{
		if ( _terrain )
		{
			// W4 Option B: gentle heightfield (all stunt features sit on its flat core). Tripwire: if
			// raycast wheels misbehave on the collision, flip vp_terrain off → this flat plane returns.
			PlaygroundTerrain.Build( _scene, _root, GroundHalf );
			return;
		}

		var go = Child( "Ground" );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/plane.vmdl" );
		renderer.Tint = GroundTan;
		go.LocalScale = new Vector3( GroundHalf * 2f * M / 100f, GroundHalf * 2f * M / 100f, 1f );

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 100f, 100f, 200f );
		collider.Center = Vector3.Down * 100f;
		collider.Static = true;
		go.Tags.Add( "road" );
	}

	// ---------------------------------------------------------------- kicker ladder (5 heights)

	/// <summary>The FIVE distinct heights laid out side by side, all launching +X, colour-coded by
	/// height with a matching marker pylon — the "pick your height" showcase. Long open runup west of
	/// it (the spawn apron + runway) so a car arrives with speed on any lane.</summary>
	static void BuildKickerLadder( Vector2 baseAtM )
	{
		float laneGap = 24f;                       // lateral spacing between the five lanes
		float y0 = -laneGap * (LadderHeights.Length - 1) * 0.5f;
		for ( int i = 0; i < LadderHeights.Length; i++ )
		{
			float h = LadderHeights[i];
			var at = new Vector2( baseAtM.x, baseAtM.y + y0 + i * laneGap );
			Kicker( at, 0f, lenM: 5f * h, widthM: 8f, heightM: h, HeightColor( h ) );
			// marker pylon off to the +Y side of the lane (out of the drive path)
			MarkerPost( new Vector2( at.x, at.y + 5.5f ), h );
		}
	}

	// ---------------------------------------------------------------- chained rhythm lines

	/// <summary>Several CHAINED lines: kickers in a row all facing +X (the ORIENTATION LAW's same-way
	/// drive line) at jumpable spacing so a car can rhythm multiple jumps. Spacing derived from the
	/// launch arc under the 1.1 g gravity: low kickers land ~14 m past the lip, mid ~19 m, the big
	/// ones farther — bases sit lip + air + a little flat before the next base.</summary>
	static void BuildRhythmLines()
	{
		// mid line: four 1.0 m kickers, ~28 m base-to-base (arc lands ~19 m past a 5 m-long ramp lip)
		ChainLine( new Vector2( -10f, 55f ), 1.0f, count: 4, spacingM: 28f, widthM: 7f );
		// fast-low line: three 0.6 m kickers, tight ~22 m spacing — quick pop-pop-pop
		ChainLine( new Vector2( -20f, 95f ), 0.6f, count: 3, spacingM: 22f, widthM: 7f );
		// BIG line (scaled ~2×): three 2.5 m kickers, ~46 m spacing so you must carry real speed to link
		ChainLine( new Vector2( -25f, 135f ), 2.5f, count: 3, spacingM: 46f, widthM: 9f );
		// south rhythm: three 1.2 m kickers for southern-field coverage, ~30 m spacing
		ChainLine( new Vector2( -30f, -45f ), 1.2f, count: 3, spacingM: 30f, widthM: 7f );
	}

	static void ChainLine( Vector2 firstBaseM, float heightM, int count, float spacingM, float widthM )
	{
		var col = HeightColor( heightM );
		for ( int i = 0; i < count; i++ )
			Kicker( new Vector2( firstBaseM.x + i * spacingM, firstBaseM.y ), 0f,
				lenM: 5f * heightM, widthM: widthM, heightM: heightM, col );
	}

	// ---------------------------------------------------------------- scattered singles

	/// <summary>Coverage singles filling the open CORNERS/EDGES, each facing OUTWARD toward its corner so
	/// its launch face meets the only realistic approach (from open field) and its back sits against the
	/// wall where nobody drives — the lone-jump form of the orientation law. Fixed placements (determinism
	/// law). Anything adjacent to another jump is an opposed pair instead (see BuildOpposedPairs).</summary>
	static void BuildScatterSingles()
	{
		// (x, y, yawDeg-facing-the-corner, heightM), spread small→large
		var singles = new (float x, float y, float yaw, float h)[]
		{
			(  150f,  150f,  45f, 2.0f ),   // NE corner
			( -140f,  140f, 135f, 1.5f ),   // NW corner
			(  150f, -150f, 315f, 3.0f ),   // SE corner (big)
			( -140f, -140f, 225f, 1.0f ),   // SW corner
			(  165f,   10f,   0f, 1.2f ),   // E edge, faces the wall
		};
		foreach ( var s in singles )
			Kicker( new Vector2( s.x, s.y ), s.yaw, lenM: 5f * s.h, widthM: 7f, heightM: s.h, HeightColor( s.h ) );
	}

	// ---------------------------------------------------------------- big air (scaled-up set-piece)

	/// <summary>The BIG-AIR launch: a huge 6 m kicker at the end of its own long runway, then a matching
	/// DOWN landing ramp across a gap so a fast car sends it and rolls out clean. A directional
	/// set-piece (one-way, like the ladder) — approached only down the runway, so a same-way launch face
	/// is correct here. The scaled-up 3× jump of the park.</summary>
	static void BuildBigAir( Vector2 baseAtM )
	{
		const float launchH = 6f, landH = 3f;
		float launchLen = 5f * launchH;                 // = 30 m base (long, gentle tangent)
		float lipX = baseAtM.x + launchLen;             // launch edge
		float gap = 34f;                                // clear-air gap over the flat
		float landLipX = lipX + gap;                    // landing ramp's high lip
		float landLen = 5f * landH;

		// launch (car flies off this, +X)
		Kicker( baseAtM, 0f, launchLen, 10f, launchH, HeightColor( launchH ) );
		// landing DOWN-ramp: yaw-180 kicker, high lip at landLipX facing the incoming car, descending +X
		Kicker( new Vector2( landLipX + landLen, baseAtM.y ), 180f, landLen, 10f, landH, HeightColor( landH ) );
	}

	// ---------------------------------------------------------------- opposed gap pairs (bidirectional)

	/// <summary>The ORIENTATION LAW's side-by-side case: pairs of kickers that sit near each other with
	/// their lips pointed AT each other across a gap, one launching +X and one launching −X. From either
	/// heading you meet a launch face — a proper double-sided jump, never a wedge back. Spread across the
	/// open mid-field for coverage from every direction.</summary>
	static void BuildOpposedPairs()
	{
		// (centreX, centreY, gapM, heightM)
		var pairs = new (float cx, float cy, float gap, float h)[]
		{
			(   10f,  20f, 20f, 1.0f ),
			(   25f, -105f, 24f, 1.5f ),
			(  -50f,  60f, 20f, 1.2f ),
			(  120f,  40f, 26f, 2.0f ),
		};
		foreach ( var p in pairs )
			OpposedGapPair( new Vector2( p.cx, p.cy ), p.gap, p.h );
	}

	/// <summary>Two kickers of equal height facing each other across a gap centred on
	/// <paramref name="centreM"/>: a +X kicker on the west side and a −X (yaw-180) kicker on the east
	/// side, both lips pointing into the gap.</summary>
	static void OpposedGapPair( Vector2 centreM, float gapM, float heightM )
	{
		float len = 5f * heightM;
		var col = HeightColor( heightM );
		// west kicker: launches +X, high lip at centre − gap/2
		Kicker( new Vector2( centreM.x - gapM * 0.5f - len, centreM.y ), 0f, len, 7f, heightM, col );
		// east kicker: launches −X (yaw 180), high lip at centre + gap/2 (base = lip + len further east)
		Kicker( new Vector2( centreM.x + gapM * 0.5f + len, centreM.y ), 180f, len, 7f, heightM, col );
	}

	// ---------------------------------------------------------------- jump onto a box

	/// <summary>A 2.2 m launch kicker, then an AIR GAP, then an elevated drivable box whose top sits a
	/// bit below the jump apex (landable at speed, missable when slow), then a curved kicker DOWN off
	/// the far edge so you are never stranded. Launch arc at ~20 m/s apexes ~4.9 m ≈ 13 m past the lip;
	/// the box top at 3.2 m is under that apex, so a fast car drops onto it and a slow car lands short
	/// on the flat.</summary>
	static void BuildJumpOntoBox( Vector2 baseAtM )
	{
		const float launchH = 2.2f, boxTop = 3.2f, boxDepth = 15f, boxWidth = 12f, downLen = 11f;
		float launchLen = 5f * launchH;                         // = 11 m
		float lipX = baseAtM.x + launchLen;                     // where the launch kicker throws you
		float boxFrontX = lipX + 12f;                           // 12 m air gap over the flat
		float boxCx = boxFrontX + boxDepth * 0.5f;
		float boxBackX = boxFrontX + boxDepth;

		// the launch kicker (car flies off this, unconnected to the box)
		Kicker( baseAtM, 0f, launchLen, boxWidth, launchH, HeightColor( launchH ) );

		// the box: a solid closed block resting on grade, drivable flat top at boxTop
		Block( new Vector3( boxCx, baseAtM.y, boxTop * 0.5f ) * M,
			new Vector3( boxDepth, boxWidth, boxTop ), BoxGrey, name: "JumpBox" );
		_boxes++;

		// down-ramp off the far edge: a yaw-180 kicker whose lip (height boxTop) lands exactly on the
		// box's back-top edge and descends +X back to grade — seamless roll-off, sealed underside.
		Kicker( new Vector2( boxBackX + downLen, baseAtM.y ), 180f, downLen, boxWidth, boxTop, BoxGrey );
	}

	// ---------------------------------------------------------------- ball pit

	/// <summary>A scatter of large dynamic spheres in a designated field — cars punt them around. Two
	/// sizes/colours, masses tuned so a ~1.2 t car visibly launches them (light enough to fly) without
	/// them jittering on the ground (heavy enough to settle). Fixed table = deterministic. 12 balls.</summary>
	static void BuildBallPit( Vector2 centreM )
	{
		// (dx, dy) offsets in metres, big? , within a ~±26 m field
		var slots = new (float dx, float dy, bool big)[]
		{
			( -22f, -14f, true ),  (  -8f,  -20f, false ), (   6f, -12f, true ),
			(  20f, -18f, false ), (  24f,   2f, true ),   (  10f,   8f, false ),
			(  -4f,  16f, true ),  ( -18f,   6f, false ),  ( -26f,  18f, true ),
			(  16f,  20f, false ), (   0f,  -2f, false ),  ( -12f,  -4f, true ),
		};
		foreach ( var s in slots )
		{
			float dia = s.big ? 2.4f : 1.4f;
			float mass = s.big ? 45f : 20f;
			var col = s.big ? new Color( 0.85f, 0.35f, 0.30f ) : new Color( 0.30f, 0.55f, 0.80f );
			DynamicBall( new Vector2( centreM.x + s.dx, centreM.y + s.dy ), dia, mass, col );
		}
	}

	static void DynamicBall( Vector2 atM, float diamM, float massKg, Color color )
	{
		float radiusM = diamM * 0.5f;
		var go = Child( "Ball" );
		go.WorldPosition = new Vector3( atM.x, atM.y, radiusM ) * M;   // rest on the ground
		go.LocalScale = Vector3.One * (diamM * M / 100f);             // sphere.vmdl is 100 u dia at scale 1

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/sphere.vmdl" );
		renderer.Tint = color;

		var collider = go.Components.Create<SphereCollider>();
		collider.Radius = 50f;   // base sphere radius in units; scales with LocalScale → radiusM

		var rb = go.Components.Create<Rigidbody>();
		rb.MassOverride = massKg;
		rb.LinearDamping = 0.2f;
		rb.AngularDamping = 0.4f;
		_balls++;
	}

	// ---------------------------------------------------------------- stunt extras

	static void BuildStuntExtras()
	{
		BuildTabletop( new Vector2( -10f, -75f ) );
		BuildStepStairs( new Vector2( 100f, -50f ) );
		BuildWallRide( new Vector2( 125f, 20f ) );
	}

	/// <summary>Classic tabletop: up-kicker → flat deck → down-kicker, deck top and both lips share
	/// deckTop so the run is seamless. Launch off the front, land on the deck or clear it to the far
	/// down-ramp.</summary>
	static void BuildTabletop( Vector2 centreM )
	{
		const float deckHalf = 7f, kickLen = 8f, deckTop = 1.4f, w = 9f;
		Kicker( new Vector2( centreM.x - deckHalf - kickLen, centreM.y ), 0f, kickLen, w, deckTop, RampGrey );
		Block( new Vector3( centreM.x, centreM.y, deckTop * 0.5f ) * M,
			new Vector3( deckHalf * 2f, w, deckTop ), RampGrey, name: "TabletopDeck" );
		_boxes++;
		Kicker( new Vector2( centreM.x + deckHalf + kickLen, centreM.y ), 180f, kickLen, w, deckTop, RampGrey );
	}

	/// <summary>A drivable staircase: shallow 0.5 m steps a car climbs at speed up to a high deck, then
	/// a curved kicker DOWN the far side. Steps are solid boxes resting on grade (no underside gap).</summary>
	static void BuildStepStairs( Vector2 baseAtM )
	{
		const int steps = 5;
		const float rise = 0.5f, depth = 4.5f, w = 9f;
		for ( int i = 0; i < steps; i++ )
		{
			float top = (i + 1) * rise;
			// each step spans grade→top and is depth deep; place its centre so the front face is climbable
			float cx = baseAtM.x + i * depth + depth * 0.5f;
			Block( new Vector3( cx, baseAtM.y, top * 0.5f ) * M, new Vector3( depth, w, top ), BoxGrey, name: "Step" );
			_boxes++;
		}
		float deckTopH = steps * rise;                       // = 2.5 m
		float deckBackX = baseAtM.x + steps * depth;
		// roll-off the back: yaw-180 kicker landing on the top step's back edge, descending +X to grade
		Kicker( new Vector2( deckBackX + 10f, baseAtM.y ), 180f, 10f, w, deckTopH, BoxGrey );
	}

	/// <summary>A banked wall-ride strip: a long slab rolled steeply so a car can carry along its face
	/// (like a straightened bowl segment). Its lower edge meets grade tangent-ish; purely a play toy.</summary>
	static void BuildWallRide( Vector2 centreM )
	{
		const float lenM = 34f, faceM = 7f, thickM = 0.5f, rollDeg = 48f;
		var go = Child( "WallRide" );
		// lift so the rolled slab's lower edge sits near grade
		go.WorldPosition = new Vector3( centreM.x, centreM.y, MathF.Sin( rollDeg.DegreeToRadian() ) * faceM * 0.5f ) * M;
		go.WorldRotation = Rotation.FromYaw( 90f ) * Rotation.FromRoll( -rollDeg );
		go.LocalScale = new Vector3( faceM, lenM, thickM ) * M / 50f;

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = BowlGrey;

		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 50f, 50f, 50f );
		collider.Static = true;
		_boxes++;
	}

	// ---------------------------------------------------------------- banked bowl (kept)

	static void BuildBankedBowl( Vector2 centreM, float radiusM, float bankDeg )
	{
		// a velodrome-style ring: N wide segments tilted to bank inward, forming a circular banked
		// wall the car can carve around. Flat centre stays drivable.
		const int segs = 24;
		float segLen = 2f * MathF.PI * radiusM / segs * 1.15f; // slight overlap, no gaps
		for ( int i = 0; i < segs; i++ )
		{
			float a = i / (float)segs * MathF.PI * 2f;
			float cx = centreM.x + MathF.Cos( a ) * radiusM;
			float cy = centreM.y + MathF.Sin( a ) * radiusM;
			float yawToCentre = MathF.Atan2( centreM.y - cy, centreM.x - cx ).RadianToDegree();

			var go = Child( "BowlSeg" );
			go.WorldPosition = new Vector3( cx, cy, MathF.Sin( bankDeg.DegreeToRadian() ) * 3.0f ) * M;
			go.WorldRotation = Rotation.FromYaw( yawToCentre ) * Rotation.FromRoll( -bankDeg );
			go.LocalScale = new Vector3( 6f, segLen, 0.4f ) * M / 50f;

			var renderer = go.Components.Create<ModelRenderer>();
			renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
			renderer.Model = Model.Load( "models/dev/box.vmdl" );
			renderer.Tint = BowlGrey;

			var collider = go.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 50f, 50f, 50f );
			collider.Static = true;
			_bowlSegs++;
		}
	}

	// ---------------------------------------------------------------- perimeter wall (kept)

	/// <summary>Solid wall enclosing the whole drivable playground (the world must NOT be drivable-off).
	/// 8 m tall, 2 m thick, sunk 3 m so it seals any gentle-terrain dip at the edge; static colliders no
	/// car can jump. Sits inside the ground pad and well outside every stunt feature.</summary>
	static void BuildPerimeterWall()
	{
		const float edge = GroundHalf - 6f;   // just inside the flat pad edge
		const float wallH = 8f, wallThick = 2f, sink = 3f;
		float len = edge * 2f + wallThick * 2f;
		float cz = (wallH - sink) * 0.5f;      // centre so the wall spans z[-sink, wallH]
		var wall = new Color( 0.50f, 0.50f, 0.54f );
		var cap = new Color( 0.38f, 0.38f, 0.42f );

		foreach ( var (x, y, sx, sy, nm) in new[]
		{
			( 0f, edge, len, wallThick, "Perimeter Wall N" ),
			( 0f, -edge, len, wallThick, "Perimeter Wall S" ),
			( edge, 0f, wallThick, len, "Perimeter Wall E" ),
			( -edge, 0f, wallThick, len, "Perimeter Wall W" ),
		} )
		{
			Block( new Vector3( x, y, cz ) * M, new Vector3( sx, sy, wallH + sink ), wall, collide: true, name: nm );
			Block( new Vector3( x, y, wallH + 0.15f ) * M, new Vector3( sx + 0.5f, sy + 0.5f, 0.3f ), cap, collide: false, name: nm + " Cap" );
		}
	}

	// ---------------------------------------------------------------- helpers

	/// <summary>Colour a feature by launch height, green (low) → red (tall), so heights read at a
	/// glance across the whole park.</summary>
	static Color HeightColor( float h )
	{
		// clamp to the height range and lerp green→yellow→red (biggest air = reddest)
		float t = MathX.Clamp( (h - 0.6f) / (4.5f - 0.6f), 0f, 1f );
		var green = new Color( 0.35f, 0.75f, 0.35f );
		var yellow = new Color( 0.92f, 0.80f, 0.28f );
		var red = new Color( 0.85f, 0.27f, 0.24f );
		return t < 0.5f ? Color.Lerp( green, yellow, t * 2f ) : Color.Lerp( yellow, red, (t - 0.5f) * 2f );
	}

	/// <summary>A tall thin colour-coded marker pylon (solid) placed OUT of the drive path to label a
	/// ladder kicker's height.</summary>
	static void MarkerPost( Vector2 atM, float heightM )
	{
		Block( new Vector3( atM.x, atM.y, 2f ) * M, new Vector3( 0.4f, 0.4f, 4f ), HeightColor( heightM ), name: "HeightPost" );
	}

	/// <summary>A flush, non-colliding dark ribbon that visually invites a speed runup (pure dressing,
	/// carries no collision — the flat pad/terrain carries the ground).</summary>
	static void Runway( Vector2 fromM, Vector2 toM, float widthM )
	{
		var mid = (fromM + toM) * 0.5f;
		var d = toM - fromM;
		float len = d.Length;
		float yaw = MathF.Atan2( d.y, d.x ).RadianToDegree();
		var go = Child( "Runway" );
		go.WorldPosition = new Vector3( mid.x, mid.y, 0.02f ) * M;
		go.WorldRotation = Rotation.FromYaw( yaw );
		go.LocalScale = new Vector3( len, widthM, 0.02f ) * M / 50f;
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.MaterialOverride = Material.Load( "materials/default.vmat" );
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = RunwayGrey;
	}

	/// <summary>Place a curved solid launch kicker (base tangent to grade → no lip; collision follows
	/// the curved face; closed underside → no drive-under gap).</summary>
	static void Kicker( Vector2 baseAtM, float yawDeg, float lenM, float widthM, float heightM, Color color )
	{
		RampKicker.Build( _scene, _root, new Vector3( baseAtM.x, baseAtM.y, 0f ), yawDeg, lenM, widthM, heightM, color );
		_ramps++;
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
		go.SetParent( _root, true );
		return go;
	}
}
