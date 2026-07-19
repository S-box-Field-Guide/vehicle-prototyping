namespace VehicleProto;

/// <summary>Layout result for the playground world — where the default car spawns.</summary>
public sealed class PlaygroundLayout
{
	public Vector3 SpawnPosition { get; set; }
	public Rotation SpawnFacing { get; set; } = Rotation.Identity;
}

/// <summary>
/// The PLAYGROUND world: a pure STUNT PARK (build 9 redesign; jump-physics rework pass 3, 2026-07-19).
/// No town, no buildings, no city road network — just a big flat playfield MAXED OUT with jumps of
/// every size (small pops up to huge-air), chained rhythm lines, double-sided mound jumps, a big-air
/// launch, a jump-onto-box, a ball pit, and a few blockout stunt extras, so it reads as a distinct map
/// from the proving-grounds measurement scene. Kept from the old layout: the flat ground core + outer
/// rolling-hills terrain (<see cref="PlaygroundTerrain"/>), the banked bowl, and the perimeter wall.
///
/// Every solid feature follows the standing DRIVABILITY LAW: entry tangent to grade (no wheel-catching
/// lip), closed underside (no drive-under trap), collision that follows the actual curved shape (the
/// <see cref="RampKicker"/> segmented tangent-box collider). Curved kickers come from RampKicker.
///
/// JUMP-PHYSICS LAWS (pass 3 — why the pass-2 park did not work in practice):
///  1. CURVATURE: kicker ground run comes from <see cref="RampKicker.LengthFor"/>, never a fixed
///     length-to-height ratio. The old L = 5·H pinned the arc radius at 13·H, and small kickers
///     bottomed the suspension into a chassis-contact WALL stop (hatch 123 G / kart 247 G measured).
///  2. NO WALL EVER FACES A FLIGHT PATH: anything a flying or short-landing car can meet is a slope.
///     The opposed gap pairs (vertical lips pointed INTO the gap = wall for any undershoot) became
///     double-sided MOUNDS; the big-air landing kicker (3 m vertical face toward the flyer) became an
///     asymmetric landing mound; the jump-onto-box front edge got a rounded roll-over nose.
///  3. STEPS ARE NOT DRIVABLE: 0.5 m risers hang every car (the 0.12 m kerb already did — 2026-07-14
///     telemetry), so the step-stairs carry a centre RIDE BOARD up the nose line; the exposed side
///     strips stay honest stairs. The wall-ride's raw 48° grade edge got a half-angle apron.
///
/// ORIENTATION LAW (pass 2, amended): jumps along one DRIVE LINE all face the SAME way (a rhythm run
/// you link); jumps that sit SIDE BY SIDE are double-sided mounds, so from any approach heading a
/// player meets a launch face, never a wedge back or a wall. Directional set-pieces (the height
/// ladder, the big-air) are the deliberate exception: each has its own one-way runway. Deterministic:
/// fixed layout + a fixed ball table, no RNG.
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
		BuildBigAir( new Vector2( 95f, -130f ) );       // huge 6 m launch + landing mound down the runway
		BuildDoubleMounds();                            // double-sided mound jumps (bidirectional)
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
			Kicker( at, 0f, lenM: RampKicker.LengthFor( h ), widthM: 8f, heightM: h, HeightColor( h ) );
			// marker pylon off to the +Y side of the lane (out of the drive path)
			MarkerPost( new Vector2( at.x, at.y + 5.5f ), h );
		}
	}

	// ---------------------------------------------------------------- chained rhythm lines

	/// <summary>Several CHAINED lines: kickers in a row all facing +X (the ORIENTATION LAW's same-way
	/// drive line) at jumpable spacing so a car can rhythm multiple jumps. Spacing re-derived for the
	/// pass-3 lengths/exit angles under the 1.1 g gravity: the flight at each line's design speed must
	/// come down on the FLAT between kickers, before the next face — the base-to-base gap is
	/// launch-length + flight + a little flat (overspeed lands low on the next curved face, which is
	/// rollable; it must never reach the face's upper half).</summary>
	static void BuildRhythmLines()
	{
		// mid line: four 1.0 m kickers, ~30 m base-to-base (≈20 m flight at the 18-20 m/s design speed)
		ChainLine( new Vector2( -10f, 55f ), 1.0f, count: 4, spacingM: 30f, widthM: 7f );
		// fast-low line: three 0.6 m kickers, tight ~25 m spacing — quick pop-pop-pop
		ChainLine( new Vector2( -20f, 95f ), 0.6f, count: 3, spacingM: 25f, widthM: 7f );
		// BIG line (scaled ~2×): three 2.5 m kickers, ~56 m spacing so you must carry real speed to link
		ChainLine( new Vector2( -25f, 135f ), 2.5f, count: 3, spacingM: 56f, widthM: 9f );
		// south rhythm: three 1.2 m kickers for southern-field coverage, ~34 m spacing
		ChainLine( new Vector2( -30f, -45f ), 1.2f, count: 3, spacingM: 34f, widthM: 7f );
	}

	static void ChainLine( Vector2 firstBaseM, float heightM, int count, float spacingM, float widthM )
	{
		var col = HeightColor( heightM );
		for ( int i = 0; i < count; i++ )
			Kicker( new Vector2( firstBaseM.x + i * spacingM, firstBaseM.y ), 0f,
				lenM: RampKicker.LengthFor( heightM ), widthM: widthM, heightM: heightM, col );
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
			Kicker( new Vector2( s.x, s.y ), s.yaw, lenM: RampKicker.LengthFor( s.h ), widthM: 7f, heightM: s.h, HeightColor( s.h ) );
	}

	// ---------------------------------------------------------------- big air (scaled-up set-piece)

	/// <summary>The BIG-AIR launch: a huge 6 m kicker at the end of its own long runway, a short
	/// clear-air gap, then an asymmetric LANDING MOUND. Pass-3 rework: the old landing piece was a
	/// lone yaw-180 kicker whose 3 m VERTICAL lip face pointed straight at the incoming flyer — any car
	/// under ~21 m/s hit a wall mid-air. The mound has no exposed vertical face anywhere: an up-slope
	/// (the rollable catch for shorts — a slow car lands on or rolls up a tangent-based curve), a crest,
	/// and a LONG shallow down-slope so the fast band (~24-30 m/s) touches down on falling ground and
	/// rolls out. A directional set-piece (one-way, like the ladder) with its own runway.</summary>
	static void BuildBigAir( Vector2 baseAtM )
	{
		const float launchH = 6f, moundH = 4.5f, gapM = 11f, downLenM = 35f, w = 12f;
		float launchLen = RampKicker.LengthFor( launchH );      // ≈ 33 m run, R ≈ 93 m
		float lipX = baseAtM.x + launchLen;                     // launch edge
		float upLen = RampKicker.LengthFor( moundH );           // ≈ 25 m catch slope
		float upBaseX = lipX + gapM;                            // mound starts after the clear-air gap
		float crestX = upBaseX + upLen;

		// launch (car flies off this, +X)
		Kicker( baseAtM, 0f, launchLen, w, launchH, HeightColor( launchH ) );
		// landing mound: up-face and down-face lips COINCIDE at the crest (no exposed vertical faces).
		// The down side runs longer than the curvature law asks (R ≈ 138 m) so ~30 m/s overshoots still
		// meet falling ground instead of flat.
		Kicker( new Vector2( upBaseX, baseAtM.y ), 0f, upLen, w, moundH, HeightColor( moundH ) );
		Kicker( new Vector2( crestX + downLenM, baseAtM.y ), 180f, downLenM, w, moundH, HeightColor( moundH ) );
	}

	// ---------------------------------------------------------------- double-sided mounds (bidirectional)

	/// <summary>The ORIENTATION LAW's side-by-side case, pass-3 form: a DOUBLE-SIDED MOUND — a +X kicker
	/// and a −X kicker whose lips COINCIDE at a shared crest. From either heading you meet a launch face,
	/// clear the crest, and the far side is a falling lander. The pass-2 "opposed gap pair" (same two
	/// kickers pulled apart, lips pointing into the gap) put each lip's vertical back face IN the other
	/// jump's flight path: at design speed a car cleared its own lip but arrived at the far lip below
	/// crest height — a wall hit. Zero gap removes the wall by construction.</summary>
	static void BuildDoubleMounds()
	{
		// (crestX, crestY, heightM) — spread across the open mid-field for coverage from every direction
		var mounds = new (float cx, float cy, float h)[]
		{
			(   20f, -15f, 1.0f ),
			(   25f, -105f, 1.5f ),
			(  -50f,  60f, 1.2f ),
			(  120f,  40f, 2.0f ),
		};
		foreach ( var p in mounds )
			DoubleMound( new Vector2( p.cx, p.cy ), p.h );
	}

	/// <summary>Two equal kickers back-to-back: the +X face's lip and the −X face's lip land on the same
	/// crest line at <paramref name="crestM"/>, so the mound's only surfaces are the two tangent-based
	/// curved faces — no vertical face anywhere.</summary>
	static void DoubleMound( Vector2 crestM, float heightM )
	{
		float len = RampKicker.LengthFor( heightM );
		var col = HeightColor( heightM );
		// west face: launches +X, lip at the crest
		Kicker( new Vector2( crestM.x - len, crestM.y ), 0f, len, 7f, heightM, col );
		// east face: launches −X (yaw 180), lip at the crest (base = crest + len further east)
		Kicker( new Vector2( crestM.x + len, crestM.y ), 180f, len, 7f, heightM, col );
	}

	// ---------------------------------------------------------------- jump onto a box

	/// <summary>A 2.2 m launch kicker, then an AIR GAP, then an elevated drivable box whose top sits a
	/// bit below the jump apex (landable at speed, missable when slow), then a curved kicker DOWN off
	/// the far edge so you are never stranded. Design window ≈ 16-22 m/s lands on the deck; faster
	/// overshoots onto the down-kicker's falling face (rollable). Pass-3 fix for the short-lander: the
	/// box's front-top edge carries a ROUNDED ROLL-OVER NOSE (quarter-round chords, motocross tabletop
	/// knuckle), so a car that arrives just below deck height deflects up onto the deck instead of
	/// slamming a vertical face. Deep shorts land flat before the box at low speed.</summary>
	static void BuildJumpOntoBox( Vector2 baseAtM )
	{
		const float launchH = 2.2f, boxTop = 3.2f, boxDepth = 15f, boxWidth = 12f, noseR = 2.2f;
		float launchLen = RampKicker.LengthFor( launchH );      // ≈ 13 m, R ≈ 40 m
		float downLen = RampKicker.LengthFor( boxTop );         // ≈ 18 m falling lander off the back
		float lipX = baseAtM.x + launchLen;                     // where the launch kicker throws you
		float boxFrontX = lipX + 12f;                           // 12 m air gap over the flat
		float boxBackX = boxFrontX + boxDepth;

		// the launch kicker (car flies off this, unconnected to the box)
		Kicker( baseAtM, 0f, launchLen, boxWidth, launchH, HeightColor( launchH ) );

		// the box, recomposed so the rounded nose REPLACES the front-top corner (a square corner would
		// poke through any added rounding): a full-depth lower slab up to boxTop − noseR, an upper box
		// set back noseR from the front, and two chord panels tracing the quarter-round between them.
		Block( new Vector3( boxFrontX + boxDepth * 0.5f, baseAtM.y, (boxTop - noseR) * 0.5f ) * M,
			new Vector3( boxDepth, boxWidth, boxTop - noseR ), BoxGrey, name: "JumpBox" );
		Block( new Vector3( boxFrontX + noseR + (boxDepth - noseR) * 0.5f, baseAtM.y, boxTop - noseR * 0.5f ) * M,
			new Vector3( boxDepth - noseR, boxWidth, noseR ), BoxGrey, name: "JumpBox Top" );
		_boxes++;

		// roll-over nose: chords of the quarter circle from the face tangent point (boxFrontX,
		// boxTop − noseR) up to the deck tangent point (boxFrontX + noseR, boxTop), two 45° steps.
		for ( int i = 0; i < 2; i++ )
		{
			float a0 = i * MathF.PI * 0.25f;                    // 0°, 45° from the vertical-face tangent
			float a1 = (i + 1) * MathF.PI * 0.25f;
			var p0 = new Vector2( boxFrontX + noseR * (1f - MathF.Cos( a0 )), boxTop - noseR + noseR * MathF.Sin( a0 ) );
			var p1 = new Vector2( boxFrontX + noseR * (1f - MathF.Cos( a1 )), boxTop - noseR + noseR * MathF.Sin( a1 ) );
			float dx = p1.x - p0.x, dz = p1.y - p0.y;
			float chord = MathF.Sqrt( dx * dx + dz * dz );
			float pitchDeg = MathF.Atan2( dz, dx ).RadianToDegree();
			// outward normal (points away from the box interior): (-dz, dx) / chord
			float nX = -dz / chord, nZ = dx / chord;
			const float thick = 0.5f;
			// centre sits half a thickness INSIDE the chord so the panel's top face lies exactly on it
			var panel = Panel(
				new Vector3( (p0.x + p1.x) * 0.5f - nX * thick * 0.5f, baseAtM.y, (p0.y + p1.y) * 0.5f - nZ * thick * 0.5f ),
				new Vector3( chord, boxWidth, thick ), Rotation.FromPitch( -pitchDeg ), BoxGrey, "JumpBox Nose" );
			panel.Tags.Add( "road" );
		}

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

	/// <summary>A staircase with a centre RIDE BOARD: the 0.5 m steps themselves are NOT climbable at
	/// any speed (a raycast wheel treats a riser as a wall — the 0.12 m kerb already hung every car,
	/// 2026-07-14 telemetry), so a 5 m-wide board runs up the stair NOSE LINE like plywood over a
	/// skate stair set: a plain ~6.3° wedge a car drives at full speed. The stairs stay exposed either
	/// side of the board so the feature still reads as stairs. Curved kicker DOWN the far side.</summary>
	static void BuildStepStairs( Vector2 baseAtM )
	{
		const int steps = 5;
		const float rise = 0.5f, depth = 4.5f, w = 9f, boardW = 5f, boardThick = 0.4f;
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

		// the ride board: top surface through every step nose (front-top edge), i.e. the line from
		// (base, rise) to (base + (steps−1)·depth, steps·rise), extended down to grade at its foot
		// (the line hits z=0 one tread-depth BEFORE the first step). Slope = rise/depth ≈ 6.3°.
		float footX = baseAtM.x - depth;                     // where the nose line meets grade
		float topX = baseAtM.x + (steps - 1) * depth;        // last nose (deck front edge)
		float runX = topX - footX, runZ = deckTopH;
		float slopeLen = MathF.Sqrt( runX * runX + runZ * runZ );
		float pitchDeg = MathF.Atan2( runZ, runX ).RadianToDegree();
		float nX = -runZ / slopeLen, nZ = runX / slopeLen;   // outward (up-slope) surface normal
		var board = Panel(
			new Vector3( (footX + topX) * 0.5f - nX * boardThick * 0.5f, baseAtM.y, runZ * 0.5f - nZ * boardThick * 0.5f ),
			new Vector3( slopeLen, boardW, boardThick ), Rotation.FromPitch( -pitchDeg ), RunwayGrey, "StairBoard" );
		board.Tags.Add( "road" );
		// flat board segment across the top deck (from the last nose to the back edge) so the ride line
		// continues over the top step at deck height
		Panel( new Vector3( (topX + deckBackX) * 0.5f, baseAtM.y, deckTopH - boardThick * 0.5f ),
			new Vector3( deckBackX - topX, boardW, boardThick ), Rotation.Identity, RunwayGrey, "StairBoard Deck" )
			.Tags.Add( "road" );

		// roll-off the back: yaw-180 kicker landing on the top step's back edge, descending +X to grade
		Kicker( new Vector2( deckBackX + 10f, baseAtM.y ), 180f, 10f, w, deckTopH, BoxGrey );
	}

	/// <summary>A banked wall-ride strip: a long slab rolled steeply so a car can carry along its face
	/// (like a straightened bowl segment). Pass-3 fix: the raw lower edge was a flat→48° grade break a
	/// car hit like a kerbed wall; a half-angle APRON strip (~24°) now runs the full length along the
	/// low edge, splitting the transition into two gentle breaks. The apron's low edge is buried below
	/// grade and its high edge tucks under the wall face, so neither edge presents a lip.</summary>
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

		// entry apron: the wall face rises toward −Y and its lower edge (z = 0) sits at
		// y = centre + cos(roll)·face/2. The apron plane runs from a buried low edge out on the flat
		// (+Y) up to a high edge tucked just UNDER the wall face, at roughly half the wall's angle.
		float lowEdgeY = centreM.y + MathF.Cos( rollDeg.DegreeToRadian() ) * faceM * 0.5f;
		const float apronThick = 0.4f;
		var apronLow = new Vector2( lowEdgeY + 1.6f, -0.1f );   // (y, z) buried at grade, out on the flat
		var apronHigh = new Vector2( lowEdgeY - 0.95f, 0.95f ); // (y, z) ~10 cm under the 48° face there
		float dy = apronHigh.x - apronLow.x, dz = apronHigh.y - apronLow.y;
		float apronW = MathF.Sqrt( dy * dy + dz * dz );         // ≈ 22° apron angle (≈ half of 48°)
		float apronRollDeg = MathF.Atan2( dz, -dy ).RadianToDegree();
		// upward surface normal in the (y,z) plane: perpendicular to the width direction (dy is
		// negative — the width vector points toward −Y as it rises)
		float nY = dz / apronW, nZ = -dy / apronW;
		var apron = Panel(
			new Vector3( centreM.x, (apronLow.x + apronHigh.x) * 0.5f - nY * apronThick * 0.5f,
				(apronLow.y + apronHigh.y) * 0.5f - nZ * apronThick * 0.5f ),
			new Vector3( apronW, lenM, apronThick ),
			Rotation.FromYaw( 90f ) * Rotation.FromRoll( -apronRollDeg ), BowlGrey.Darken( 0.12f ), "WallRide Apron" );
		apron.Tags.Add( "road" );
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

	/// <summary>A rotated solid block, position given in METRES (unlike <see cref="Block"/>, whose
	/// callers pre-multiply). Used for the pitched/rolled ride surfaces (stair board, roll-over nose,
	/// wall-ride apron); always collides — these are surfaces cars drive on.</summary>
	static GameObject Panel( Vector3 centreMeters, Vector3 sizeMeters, Rotation rotation, Color color, string name )
	{
		var go = Block( centreMeters * M, sizeMeters, color, collide: true, name: name );
		go.WorldRotation = rotation;
		return go;
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
