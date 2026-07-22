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
		Runway( new Vector2( -70f, -130f ), new Vector2( 18f, -130f ), 14f ); // big-air runway (south-east)

		BuildKickerLadder( new Vector2( -60f, 0f ) );   // the FIVE showcase heights, side by side (one-way)
		BuildRhythmLines();                             // chained same-direction kicker lines you rhythm
		// base 95 -> 60 (law pass) -> 20 (easement pass): each profile stretch ran the set-piece
		// closer to the perimeter wall at 254; at 20 the easement chain ends at ~239.
		BuildBigAir( new Vector2( 20f, -130f ) );       // huge 6 m launch + landing mound down the runway
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

	/// <summary>
	/// Build the stunt content INTO the proto world (owner intent 2026-07-19: players load into the
	/// town, drive east through the gate and spur onto the hardpack, and the stunt park is just
	/// THERE - no world switch). Three zones on the proving-grounds hardpack (world x 500-1500,
	/// y -280..320), each zone rectangle authored with 15+ m margins from every station corridor
	/// (skidpad, drag/brake/topspeed lane, slalom, ramps/washboard/hill lane, lowgrip, jturn,
	/// banked curve, crash lane, spur entry). BATTERY LAW: nothing may be placed outside these
	/// rectangles, no station moves, TestTrack.cs untouched.
	///   NORTH BAND      x 720-1240, y 80..318   (ladder + big-air set-pieces, freeform scatter)
	///   SOUTH-EAST      x 1060-1300, y -278..-85 (bowl, wall-ride, ball pit, smooth entry mound)
	///   SOUTH-WEST      x 505-625,  y -270..-75  (welcome zone off the spur: jumpbox, tabletop, mounds)
	///
	/// REDESIGN 2026-07-21 (owner: "more ramps spread out, not so uniform... facing multiple
	/// positions so no matter where you're driving someone can go off a ramp... get rid of the
	/// leftover rigid ramps that are just hard angles in the back-right corner... keep the ball
	/// pit... make that whole area more natural to drive around"). Three changes:
	///  1. NORTH BAND DE-GRIDDED. The old three parallel rhythm rows + four grid-placed mounds are
	///     gone; in their place a FREEFORM SCATTER: two fans (west-centre N/NE/E, east S-of-curve
	///     E/SE/NNW), two bidirectional mounds, and coverage singles facing N, S, W, SE across the
	///     open field. Headings now span the full compass, so a car on any approach has a launch
	///     face lined up (the ORIENTATION LAW's lone-jump/side-by-side forms, scattered not rowed).
	///  2. HARD-ANGLE CLUSTER REMOVED. The step-stairs (5 stacked 0.5 m VERTICAL riser boxes) were
	///     the "rigid ramps, no slope, just hard angles" the owner flagged in the SE (back-right as
	///     you drive in). A 0.5 m riser is a wall to a raycast wheel (2026-07-14 telemetry) and a
	///     near-vertical facet is exactly this week's chassis wall-stop class; removing them is
	///     safety, not just looks. Replaced by a smooth bidirectional mound + a pop in the open
	///     pocket east of the wall-ride. (The centre ride-board hack the stairs carried is retired
	///     with them.) BuildStepStairs stays only in the dev-only Build() world.
	///  3. KEPT (earned live-verification): bowl (with its NE entry-mouth escape route), wall-ride
	///     quarter-pipe, big-air + landing mound, ball pit (12 balls), jumpbox, tabletop, mounds,
	///     and the five-height calibrated LADDER (the ramp-physics measurement instrument), tucked
	///     along the band's west edge as its own coherent +X lane, clearly apart from the scatter.
	///
	/// GEOMETRY LAW: every kicker length comes from RampKicker.LengthFor(h) (restored MinRadiusM 90,
	/// the "poppy" default of branch ramp-highspeed-hitch); footprints/seams use the easement run
	/// RampKicker.GroundRunFor (~28% past the authored length at blend 0.5). No length is hardcoded,
	/// so a future radius-law change flows through untouched.
	///
	/// FULL-BORE (46 m/s) LANDING-CORRIDOR + OVERLAP AUDIT (offline, tools/layout_validate.py, the
	/// exact LengthFor/EasementCore/FlightRangeM port; every corridor = lip -> ballistic landing at
	/// 46 m/s under 1.1 g, swept at kicker width, tested against every other feature footprint, the
	/// banked-curve wall, the external hill-ladder ramps, and the drivable-slab bounds). RESULT:
	/// zero corridor-into-obstacle, zero corridor-into-kicker-body, zero footprint-out-of-zone
	/// across the 20 north-band + 3 SE kickers.
	///   NORTH BAND (re-audited under the round-3 speed ratings; tools/layout_validate.py, still
	///     0 issues): the ONLY external collidable obstacle in reach is the banked-curve ring wall
	///     (x 1251-1303, y 167-227). Every east/north-east corridor stays clear of it: the closest
	///     approaches are fanB_E landing (1229,150) and scSE landing (1284,92), both south of the
	///     wall's y 167 by 17+ m or east of it on open flat. Ladder lanes (exit 4.7-11.3 deg under
	///     the 46 rating) land x 792-887 each on its own lane's flat. Big-air (base 760,300)
	///     overshoot meets its own lengthened down-slope. Mound overshoots and the S/N pops land on
	///     open hardpack (sPop lands (1000,29) on the collide-false drag pad; nEdge/scNE/scN2 land
	///     inside the slab with 20+ m to the y 320 edge). Drag-strip distance boards and
	///     skidpad/jturn/lowgrip markers are collide-false, so south-facing corridors onto that
	///     flat are safe.
	///   SE ZONE: the entry mound (crest 1100,-108, h1.2) throws E to (1170,-108) and W to
	///     (1030,-108), both north of the bowl (y -166), the bowl-mouth drive-in lane (y < -120),
	///     the wall-ride (x 1223-1257) and the external hill ramps (x 1026-1054, y < -145). The E
	///     pop (1280,-110, N) lands (1280,-29) on open flat east of the wall-ride. Bowl NE mouth
	///     and wall-ride ride-face approaches left clear.
	///   SW ZONE: unchanged (owner: keep). Re-verified under floor-90: the shorter runs fit with
	///     seams auto-flush (LengthFor/Run drive every seam), and the only launches that leave the
	///     slab are the west-edge ones (jumpbox base + m1 east face) landing onto the DRIVABLE spur
	///     road west of x 505, as before.
	/// REGIME-B / FACE-LOAD SPEED RATING (round-3 feel pass, 2026-07-21 evening; LIVE-UNVERIFIED).
	/// The owner reproduced the ramp hitch at 32 m/s WITH fps capped to the tick rate, proving the
	/// residual is REAL rendered motion. Offline quantification (tools/ramp_bottoming_port.py) at a
	/// 32 m/s entry on the floor-90 faces: NO bottoming anywhere (sink 36-45 mm, zero bottomed
	/// ticks); the felt jolt is the CENTRIPETAL CROUCH - v^2/R = 11.4 m/s^2 (1.06 g on top of
	/// gravity, about 2.1x static axle load) held for the face crossing. The only dial that lowers
	/// face load at speed is the radius, so the fast-approach features carry a per-feature
	/// designSpeedMs (RampKicker.RadiusFor rating):
	///   LADDER lanes (runway-fed, the full-bore instrument): 46 m/s -> R 180, face load at 32 m/s
	///     drops 11.4 -> 5.7 m/s^2 (sink 15-19 mm), at 46 stays inside the law margin (11.8).
	///   BIG-AIR (runway marquee): 53 m/s -> H6 launch unchanged (52H already 312), H4.5 catch
	///     234 -> 240 (+2.4%): near-free correctness.
	///   SCATTER / MOUNDS / SE POP (interior arrivals): 35 m/s -> R 104. Honest note: this trims
	///     the 32 m/s crouch only ~15% (sink 38-45 -> 32-35 mm); interior pop is kept on purpose.
	///     If the owner still reports hitch on a specific scatter face at 70+ mph, the next notch
	///     is 46 on that call site (one line).
	///   SW welcome zone: unrated (0, poppy default) - spur-road approach speeds, owner: keep.
	/// A ramp still pitches and crouches BY DESIGN at speed; the target of this rating is no harsh
	/// jolt (load spike + deep sink), not zero motion.
	/// LIVE-UNVERIFIED: geometry math only. An editor pass must drive the scatter from several
	/// headings, confirm the SE mound/pop and the removed-stairs area feel clean, and re-check the
	/// ladder + big-air at full bore. The dev-only Build() world is unchanged.
	/// Both entries reset the shared statics, so either can run in a session without stale state.
	/// </summary>
	public static void BuildProtoStuntZones( Scene scene )
	{
		_scene = scene;
		_root = scene.CreateObject();
		_root.Name = "Stunt Zones";
		_terrain = false;
		_ramps = _bowlSegs = _balls = _boxes = 0;

		// ---- NORTH BAND (x 720-1240, y 80..318): two directional set-pieces + freeform scatter ----
		// calibrated instrument: the five-height ladder, tucked along the WEST edge, its own +X lane
		Runway( new Vector2( 690f, 132f ), new Vector2( 730f, 132f ), 14f );
		BuildKickerLadder( new Vector2( 735f, 132f ) );
		// directional set-piece: big-air down its own runway along the north strip
		Runway( new Vector2( 718f, 300f ), new Vector2( 758f, 300f ), 12f );
		BuildBigAir( new Vector2( 760f, 300f ) );
		// west-centre fan (angular coverage N / NE / E)
		Scatter( new Vector2( 905f, 175f ), 90f, 1.0f );
		Scatter( new Vector2( 915f, 150f ), 55f, 1.2f );
		Scatter( new Vector2( 905f, 120f ), 10f, 1.0f );
		// bidirectional mounds (each launches from two opposite headings, no vertical face)
		DoubleMound( new Vector2( 1050f, 120f ), 1.5f, ScatterDesignSpeedMs );
		DoubleMound( new Vector2( 1080f, 235f ), 1.2f, ScatterDesignSpeedMs );
		// east fan (E / SE / NNW), all kept south of the banked-curve corridor
		Scatter( new Vector2( 1150f, 150f ), 0f, 1.0f );
		Scatter( new Vector2( 1160f, 120f ), 315f, 1.0f );
		Scatter( new Vector2( 1140f, 175f ), 105f, 1.2f );
		// coverage singles filling the open pockets, varied headings
		Scatter( new Vector2( 1165f, 225f ), 90f, 0.6f );    // N pop, mid-field
		Scatter( new Vector2( 1000f, 108f ), 270f, 1.0f );   // S pop onto the open drag flat
		Scatter( new Vector2( 1210f, 265f ), 180f, 1.0f );   // NE corner, faces W back across the field
		Scatter( new Vector2( 980f, 272f ), 315f, 0.6f );    // N-centre small SE pop
		Scatter( new Vector2( 1205f, 92f ), 0f, 1.0f );      // SE of band, faces E (open, S of the curve)

		// ---- SOUTH-EAST ZONE (x 1060-1300, y -278..-85): bowl, wall-ride, ball pit + smooth entry ----
		BuildBankedBowl( new Vector2( 1150f, -200f ), radiusM: 34f, bankDeg: 26f );
		BuildWallRide( new Vector2( 1240f, -120f ) );
		// step-stairs REMOVED here (the hard-angle cluster the owner flagged; see the summary). A
		// smooth bidirectional mound takes their place at the entry; a low pop sits in the open
		// pocket east of the wall-ride so the corner reads varied without a wall a car can meet.
		DoubleMound( new Vector2( 1100f, -108f ), 1.2f, ScatterDesignSpeedMs );
		Scatter( new Vector2( 1280f, -110f ), 90f, 1.0f );
		BuildBallPit( new Vector2( 1240f, -220f ) );

		// ---- SOUTH-WEST WELCOME ZONE (x 505-625, y -270..-75): unchanged (owner: keep) ----
		BuildJumpOntoBox( new Vector2( 505f, -110f ) );
		BuildTabletop( new Vector2( 560f, -180f ) );
		DoubleMound( new Vector2( 540f, -140f ), 1.0f );
		DoubleMound( new Vector2( 600f, -230f ), 1.2f );

		Log.Info( $"[vp] stunt zones built: {_ramps} kickers, {_boxes} boxes/platforms, " +
			$"banked bowl {_bowlSegs} segs, {_balls} balls" );
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
	/// <summary>Per-feature face-load speed ratings (see the REGIME-B / FACE-LOAD paragraph in the
	/// class doc): the fastest realistic arrival each feature class must stay smooth at. Passed to
	/// RampKicker.LengthFor as designSpeedMs.</summary>
	const float LadderDesignSpeedMs = 46f;   // runway-fed full-bore instrument
	const float BigAirDesignSpeedMs = 53f;   // runway marquee; near no-op on its big radii
	const float ScatterDesignSpeedMs = 35f;  // interior arrivals; keeps pop

	static void BuildKickerLadder( Vector2 baseAtM )
	{
		float laneGap = 24f;                       // lateral spacing between the five lanes
		float y0 = -laneGap * (LadderHeights.Length - 1) * 0.5f;
		for ( int i = 0; i < LadderHeights.Length; i++ )
		{
			float h = LadderHeights[i];
			var at = new Vector2( baseAtM.x, baseAtM.y + y0 + i * laneGap );
			Kicker( at, 0f, lenM: RampKicker.LengthFor( h, LadderDesignSpeedMs ), widthM: 8f, heightM: h, HeightColor( h ) );
			// marker pylon off to the +Y side of the lane (out of the drive path)
			MarkerPost( new Vector2( at.x, at.y + 5.5f ), h );
		}

		// (Round-6 A/B mesh twin removed 2026-07-21: verdict landed — mesh integ ratio 1.00 vs
		// boxes 0.31 on owner-driven runs — and ColliderMode.SolidMesh is now the RampKicker
		// default for every kicker, so the twin was redundant. History in RampKicker's docs.)
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
		// mid line: four 1.0 m kickers, 36 m base-to-base. Was 30: at the ~21.4 m/s the test's
		// full-throttle commit produces, the flight off kicker 1 spans ~25-27 m past the lip, which
		// came down at kicker 2's base or low face (the south line measured the failure at 79 G; this
		// line had the same thin margin on paper). 36 m puts that landing on flat with 3+ m to spare;
		// slower rhythm speeds land earlier and link as before.
		ChainLine( new Vector2( -10f, 55f ), 1.0f, count: 4, spacingM: 36f, widthM: 7f );
		// fast-low line: three 0.6 m kickers, tight ~25 m spacing — quick pop-pop-pop
		ChainLine( new Vector2( -20f, 95f ), 0.6f, count: 3, spacingM: 25f, widthM: 7f );
		// BIG line (scaled ~2×): three 2.5 m kickers, ~56 m spacing so you must carry real speed to link
		ChainLine( new Vector2( -25f, 135f ), 2.5f, count: 3, spacingM: 56f, widthM: 9f );
		// south rhythm: three 1.2 m kickers for southern-field coverage, 40 m spacing. Was 34: at the
		// ~21.4 m/s the test's full-throttle commit produces, the flight off kicker 1 spans ~25-27 m
		// past the lip and came down on kicker 2's base or low face (measured 79 G near-stop into its
		// face). 40 m puts that landing on flat with 3+ m to spare; slower rhythm speeds land earlier
		// and link as before. y=-38, not -45: at -45 the 7 m-wide kickers (edges y -48.5..-41.5)
		// laterally overlapped the ladder's 0.6 m lane landing corridor (lane at y=-48, kickers 8 m
		// wide, so edges -52..-44) — a car landing off the 0.6 lane plowed into the first south
		// kicker's side (measured 76 G spike + stuck). At -38 the line edge sits 2.5 m clear of the
		// lane edge and 3.5 m clear of the jump-onto-box corridor (y -31..-19).
		ChainLine( new Vector2( -30f, -38f ), 1.2f, count: 3, spacingM: 40f, widthM: 7f );
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
		// downLenM 35 -> 55 (full-bore law pass): at 46 m/s entry the flight overshoots the whole
		// mound and used to land FLAT ~50 m past its end at vz ~14 m/s (60 G measured at 45.1).
		// With the 11.3-degree exit and the longer down-slope, the full-bore trajectory intercepts
		// the falling board (~35-45 G effective); mid-speed (~26 m/s) still lands on the up-face.
		const float launchH = 6f, moundH = 4.5f, gapM = 11f, downLenM = 55f, w = 12f;
		// speed-rated at 53 (BigAirDesignSpeedMs): H6 launch unchanged (52H = 312 already exceeds the
		// rating), H4.5 catch mound 234 -> 240 (+0.6 m authored). Seams flow through Run() as always.
		float launchLen = RampKicker.LengthFor( launchH, BigAirDesignSpeedMs );  // authored ≈ 61 m, easement run ≈ 72 m
		float lipX = baseAtM.x + Run( launchLen, launchH );     // launch edge (REAL easement run)
		float upLen = RampKicker.LengthFor( moundH, BigAirDesignSpeedMs );       // authored ≈ 46 m catch slope
		float upBaseX = lipX + gapM;                            // mound starts after the clear-air gap

		// launch (car flies off this, +X)
		Kicker( baseAtM, 0f, launchLen, w, launchH, HeightColor( launchH ) );
		// landing mound as ONE merged solid (asymmetric crest: the down side runs longer than the
		// curvature law asks so fast overshoots still meet falling ground instead of flat).
		// Formerly two crest-mated kickers - buried-lip sunken-belly hazard, see DoubleMound's doc.
		RampKicker.BuildCrested( _scene, _root, new Vector3( upBaseX, baseAtM.y, 0f ), 0f,
			upLen, w, moundH, HeightColor( moundH ), StuntProfile, downLengthM: downLenM );
		_ramps += 2;
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
		// (crestX, crestY, heightM) — spread across the open mid-field for coverage from every direction.
		// The 1.2 mound moved y=60 to y=64: at 60 its 7 m width reached y 56.5, only ~0.6 m clear of the
		// mid rhythm line's approach lane at y=55 (a test car snagged its west base corner and wedged,
		// measured 700+ stuck ticks). y=64 gives the lane ~4.6 m clearance and keeps 2 m to the mid
		// line's kicker edge.
		var mounds = new (float cx, float cy, float h)[]
		{
			(   20f, -15f, 1.0f ),
			(   25f, -105f, 1.5f ),
			(  -50f,  64f, 1.2f ),
			(  120f,  40f, 2.0f ),
		};
		foreach ( var p in mounds )
			DoubleMound( new Vector2( p.cx, p.cy ), p.h );
	}

	/// <summary>Two equal faces meeting at a crest, built as ONE merged solid
	/// (<see cref="RampKicker.BuildCrested"/>). Formerly two back-to-back kickers whose buried
	/// vertical lip faces met at the crest: a bottomed (regime-B) car's sunken belly caught the
	/// opposing lip wall there — flight-recorder verdict 2026-07-21, 39.7→9.9 m/s in one tick,
	/// contact normal (-1,0,0), on this builder's (1050,120) mound. The merged solid has no
	/// internal faces for the belly to catch.</summary>
	static void DoubleMound( Vector2 crestM, float heightM, float designSpeedMs = 0f )
	{
		float len = RampKicker.LengthFor( heightM, designSpeedMs );
		float run = Run( len, heightM );   // easement run: the faces must still MEET at the crest
		var col = HeightColor( heightM );
		RampKicker.BuildCrested( _scene, _root, new Vector3( crestM.x - run, crestM.y, 0f ), 0f,
			len, 7f, heightM, col, StuntProfile );
		_ramps += 2;
	}

	// ---------------------------------------------------------------- jump onto a box

	/// <summary>A 3.0 m launch kicker, then an AIR GAP, then an elevated drivable box whose top sits
	/// just above the lip (0.2 m), then a curved kicker DOWN off the far edge so you are never
	/// stranded. Speed bands (full-bore law, 11.3-degree exit, measured/derived): below ~13 m/s
	/// lands flat in the gap; ~14-20 arrives below deck and deflects up the roll-over NOSE
	/// (quarter-round chords, motocross tabletop knuckle - no vertical face in the flight path
	/// above z 1); ~20-28 lands on the deck; above ~28 sails the whole box and lands on the
	/// down-kicker's shallow foot or flat. KNOWN RESIDUAL (pre-existing class): a narrow ~12.5-14
	/// m/s airborne band crosses the gap below the nose (z &lt; 1) and meets the box's lower front
	/// face; those cars barely launched and arrive slow. See the launchH note below for why 3.0
	/// (the 2.2 m version had a wheel-graze band on the front-top corner at exactly the entry
	/// speed the zone's runup geography delivers).</summary>
	static void BuildJumpOntoBox( Vector2 baseAtM )
	{
		// launchH 2.2 -> 3.0 (full-bore law pass, measured): with the 2.2 m lip the descent-to-deck
		// point crossed the box's BACK edge at ~31-35 m/s entry, so that band GRAZED the front-top
		// corner with the wheels (~0.6 m corner clearance minus droop; measured 159 G flip at 33.4,
		// which is exactly the entry the zone's max west runup delivers). At 3.0 the lip sits only
		// 0.2 m under the deck, so the front-corner crossing clears by 1.1+ m at every speed from
		// ~20 m/s to terminal: the graze band is gone structurally, undershoots still deflect off
		// the nose, and deep shorts land flat in the gap as before.
		const float launchH = 3.0f, boxTop = 3.2f, boxDepth = 15f, boxWidth = 12f, noseR = 2.2f;
		float launchLen = RampKicker.LengthFor( launchH );      // authored ≈ 30 m, easement run ≈ 36 m
		float downLen = RampKicker.LengthFor( boxTop );         // authored ≈ 32 m falling lander off the back
		float lipX = baseAtM.x + Run( launchLen, launchH );     // where the launch kicker throws you
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
		Kicker( new Vector2( boxBackX + Run( downLen, boxTop ), baseAtM.y ), 180f, downLen, boxWidth, boxTop, BoxGrey );
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
	/// down-ramp. Pass-3 fix: this was the last kicker still using a hand-picked run length below the
	/// curvature law (8 m at 1.4 m lip = R 23.6 m, vs the law's 28.6 m); measured on the hatch at 21.5
	/// m/s: 112 G face stop with only 0.18 s of air. kickLen now comes from LengthFor, which keeps the
	/// deck seam flush because the lip height still equals deckTop.</summary>
	static void BuildTabletop( Vector2 centreM )
	{
		// ONE merged solid (up-face + deck + down-face) since 2026-07-21: the old up-kicker +
		// deck Block + down-kicker trio buried two vertical lip faces at the deck seams, the
		// same sunken-belly wall-stop hazard as DoubleMound's crest (see that builder's doc).
		const float deckHalf = 7f, deckTop = 1.4f, w = 9f;
		float kickLen = RampKicker.LengthFor( deckTop );
		float kickRun = Run( kickLen, deckTop );   // easement run keeps the deck seams flush
		RampKicker.BuildCrested( _scene, _root,
			new Vector3( centreM.x - deckHalf - kickRun, centreM.y, 0f ), 0f,
			kickLen, w, deckTop, RampGrey, StuntProfile, deckM: deckHalf * 2f );
		_ramps += 2;
		_boxes++;
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
		Kicker( new Vector2( deckBackX + Run( 10f, deckTopH ), baseAtM.y ), 180f, 10f, w, deckTopH, BoxGrey );
	}

	/// <summary>A curved QUARTER-PIPE wall-ride bank: the <see cref="RampKicker"/> tangent-arc profile
	/// rotated to climb −Y, so the face rises from a grade-tangent base line at y = centre+11.2 to a
	/// 5 m top edge at y = centre, rideable along X for the full 34 m. Round-2 fix: the old slab +
	/// half-angle apron still met the ground in planar CREASES (flat, 22°, 48°), and a crease arrests
	/// a car at ANY approach angle (measured: hatch 128 G dead stop on a 4° glancing line; a wide car
	/// straddles the crease, one axle per plane, and wedges). The run length is hand-picked BELOW the
	/// curvature law, and that is correct here: LengthFor(5) would give a ~27.7 m gentle bank (~20°
	/// exit, not a wall-ride), while L=11.2 at H=5 gives R ≈ 15 m and a ~48° exit, the wall-ride
	/// character. The law protects HEAD-ON climbs; a wall-ride is ridden ALONG the face, so the wheel
	/// path's climb component is a fraction of ride speed and the effective face load stays low. Cars
	/// that do drive straight at it now meet a smooth tangent curve, not a crease. A back-side WEDGE
	/// covers the 5 m vertical back face so southern approaches meet a slope.</summary>
	static void BuildWallRide( Vector2 centreM )
	{
		// yaw −90 points the kicker's climb axis (local +X) toward −Y: base tangent line at
		// y = centre+11.2, lip at y = centre. The width axis (local Y, centred by construction per
		// RampKicker's LOCAL FRAME doc) maps to world X, so no x offset: the 34 m ride length spans
		// x [centre−17, centre+17] as-is. RampKicker carries its own colliders and "road" tag.
		// PROFILE: deliberately PURE ARC, not easement. The wall-ride is ridden ALONG the face at
		// a shallow climb component (its documented curvature-law exemption); an easement blend
		// would stretch the quarter-pipe footprint and soften the 48-degree wall character.
		RampKicker.Build( _scene, _root, new Vector3( centreM.x, centreM.y + 11.2f, 0f ), -90f, 11.2f, 34f, 5f, BowlGrey );
		_ramps++;

		// back wedge: a pitched panel from the top edge line (y = centre, z = 5) descending south to
		// grade at y = centre − 12.5, full 34 m along X, so approaches from the south meet a ~21.8°
		// slope instead of the 5 m vertical back face.
		const float wedgeRise = 5f, wedgeRun = 12.5f, wedgeThick = 0.5f;
		float wedgeA = MathF.Atan2( wedgeRise, wedgeRun );                     // ≈ 21.8°
		float wedgeW = MathF.Sqrt( wedgeRun * wedgeRun + wedgeRise * wedgeRise );  // ≈ 13.46 m slope width
		// the surface rises toward +Y (toward the wall's top edge), so the up-normal leans −Y
		float nY = -MathF.Sin( wedgeA ), nZ = MathF.Cos( wedgeA );
		// centre sits half a thickness inside the surface midpoint (centre.x, centre.y − 6.25, 2.5);
		// roll sign flips vs the old apron because this surface rises toward +Y, not −Y
		var wedge = Panel(
			new Vector3( centreM.x, centreM.y - wedgeRun * 0.5f - nY * wedgeThick * 0.5f,
				wedgeRise * 0.5f - nZ * wedgeThick * 0.5f ),
			new Vector3( wedgeW, 34f, wedgeThick ),
			Rotation.FromYaw( 90f ) * Rotation.FromRoll( wedgeA.RadianToDegree() ), BowlGrey.Darken( 0.12f ), "WallRide Back" );
		wedge.Tags.Add( "road" );
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
			// entry/exit mouth: skip the three segments facing the open field (NE, toward the park
			// centre). Measured without it: cars that dropped into the bowl could not climb out at
			// low speed (700+ stuck ticks hatch; the kart circled and hit the ring at 117 G). A real
			// velodrome has an apron opening; cars now roll in and out at grade through the mouth.
			if ( i is 2 or 3 or 4 )      // segs 2/3/4 are centred on 30/45/60°, the NE-facing arc for this SW-corner bowl
				continue;
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

	/// <summary>Every stunt kicker uses the clothoid EASEMENT profile (ramp-flow pass: the pure
	/// arc's curvature step ate ~16% of full-bore speed as a hitch at the base; see RampKicker.
	/// RampProfile). The proving-ground ramps stay pure Arc via the default (battery law).</summary>
	const RampKicker.RampProfile StuntProfile = RampKicker.RampProfile.Easement;

	/// <summary>REAL ground run of a stunt kicker authored as (lenM, hM): the easement profile
	/// runs ~29% longer than the authored law length (blend 0.5). Chained placements (deck seams,
	/// mound crests, gap lips, down-ramp bases) must use this, never the authored length.</summary>
	static float Run( float lenM, float hM ) => RampKicker.GroundRunFor( lenM, hM, StuntProfile );

	/// <summary>A scatter single: one launch kicker at a chosen heading and height, length from the
	/// curvature law (RampKicker.LengthFor, so a law change flows through). Colour-coded by height.
	/// The freeform-scatter form of the orientation law - each faces its own way for multi-directional
	/// coverage; its full-bore landing corridor is validated clear in BuildProtoStuntZones' audit.</summary>
	/// <summary>Proto-park coverage single, speed-rated at <see cref="ScatterDesignSpeedMs"/>
	/// (interior arrivals; see the FACE-LOAD paragraph in the class doc).</summary>
	static void Scatter( Vector2 baseAtM, float yawDeg, float heightM, float widthM = 8f )
		=> Kicker( baseAtM, yawDeg, RampKicker.LengthFor( heightM, ScatterDesignSpeedMs ), widthM, heightM, HeightColor( heightM ) );

	/// <summary>Place a curved solid launch kicker (base tangent to grade → no lip; collision follows
	/// the curved face; closed underside → no drive-under gap).</summary>
	static void Kicker( Vector2 baseAtM, float yawDeg, float lenM, float widthM, float heightM, Color color )
	{
		RampKicker.Build( _scene, _root, new Vector3( baseAtM.x, baseAtM.y, 0f ), yawDeg, lenM, widthM, heightM, color, StuntProfile );
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
