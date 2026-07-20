namespace VehicleProto;

/// <summary>
/// Builds a curved SOLID launch kicker. The VISUAL is a procedural mesh (the
/// <see cref="PlaygroundTerrain"/> <c>Model.Builder</c> idiom); the COLLISION is a stack of solid
/// convex TANGENT BOXES, one per arc segment, each pitched to the local surface slope.
///
/// PROFILE: a circular arc TANGENT to the ground at the base — zero lip where a wheel first meets it,
/// the same drivability law the flush roads follow — that steepens toward a vertical back lip of
/// height <c>H</c> over run <c>L</c>. The car drives up smoothly from grade and launches off the back
/// edge.
///
/// WHY SEGMENTED CONVEX BOXES (build 9 pass 2 — collision fix): the old collider was a single thin
/// triangle-mesh SHELL fed straight into <c>Model.Builder.AddCollisionMesh</c>. Against this map's
/// raycast/shapecast wheels that shell mis-behaves — a small kicker at full speed acted as a WALL
/// instead of a launch: a thin concave mesh gives the downward wheel shapecast an unreliable hit, the
/// chassis belly catches the face, and there is no solid volume beneath to ride on. Replacing it with
/// a set of SOLID convex boxes fixes every failure mode of that shell at once:
///   • Each box's TOP face lies exactly on its arc segment, so the collision surface follows the curve
///     face-for-face at every scale — the base segment is genuinely ~0° tangent, the way the render is.
///   • Boxes are primitive convex shapes the physics engine never hulls/AABB-simplifies (the mesh
///     shell was the simplification suspect).
///   • Each box is a SOLID volume buried below grade, so overlapping neighbours form one gapless solid
///     the wheel shapecast lands on cleanly and the belly rolls over — no thin-shell penetration.
/// The visual mesh is unchanged; only the collider architecture moved from one shell to N boxes.
///
/// LOCAL FRAME: the base front edge sits at the local origin, the run goes along +X to the lip at
/// x=L, the width W is centred on Y, and the surface rises in +Z. Position/orient the whole piece
/// with <paramref name="atM"/> (metres) + <paramref name="yawDeg"/> (a down-ramp is the same kicker
/// placed at its far end with yawDeg+180). Deterministic: pure function of the size args, no RNG.
/// </summary>
public static class RampKicker
{
	const float M = Units.MetersToUnits;

	/// <summary>Face profile selection. Arc = the original circular arc, tangent at the base but
	/// with curvature STEPPING from 0 to 1/R in one segment crossing; on TIGHT radii (the old
	/// R = 14H+9 law, R ~ 13-37 m) that step is a hard jounce that scrubs speed. Easement =
	/// clothoid-blended: curvature rises LINEARLY from 0 to 1/R over the first
	/// <see cref="EasementBlend"/> of the run, then holds, so the suspension load onset is ~30x
	/// gentler at the base (0.007 deg/segment vs the arc's 0.21 deg) - it kills the base JERK the
	/// owner feels as a "hitch". MEASURED (2026-07-20 A/B, hatch jump, 2.0 ladder face): under the
	/// current R(H)=max(90,52H) law both profiles already retain ~98% of NET face speed at 38-45 m/s
	/// (the R>=90 floor made the pure-arc step small enough that the dampers absorb it), so the
	/// easement's win is the smoother onset feel, not net speed - and its uniform-scale tightens the
	/// effective radius ~3.6% (scale 0.964), a hair MORE sustained face load. BATTERY LAW: Easement
	/// is OPT-IN and the default stays Arc; the proving-ground ramps (TestTrack) keep their measured
	/// pure-arc geometry byte-identical.</summary>
	public enum RampProfile { Arc, Easement }

	/// <summary>Fraction of the easement run over which curvature blends 0 to 1/R. 0.5 measured
	/// clean at full bore (2.0 ladder, 38-45 m/s: no flips, ~11-15 deg landing pitch, seams flush,
	/// footprints in-zone). Higher = smoother onset but longer/tighter tail; 0.5 is the kept value.</summary>
	public const float EasementBlend = 0.5f;

	/// <summary>Collider/facet resolution for easement kickers: one segment per ~this many metres
	/// of run (finer facets matter at 46 m/s, where an arc-16 facet joint arrives every ~28 ms),
	/// hard-capped at <see cref="MaxSegments"/>.</summary>
	public const float EasementSegmentMeters = 0.7f;
	public const int MaxSegments = 40;

	/// <summary>The REAL ground run of a kicker built with <paramref name="profile"/> from the
	/// authored (lengthM, heightM) pair. Arc: the authored length itself. Easement: LONGER (the
	/// blend spends the early run at low curvature; ~29% at blend 0.5), integrated exactly like
	/// Build does (verified seam-flush: GroundRunFor at segs=16 matches Build's esegs run to <1e-3 m
	/// across every stunt feature). Chained builders (tabletop seams, mound crests, box gaps,
	/// down-ramp lips) must use THIS, not the authored length, or their seams drift.</summary>
	public static float GroundRunFor( float lengthM, float heightM, RampProfile profile )
	{
		if ( profile == RampProfile.Arc )
			return lengthM;
		return EasementCore( lengthM, heightM, 16 ).runM;
	}

	/// <summary>
	/// MINIMUM-RADIUS LAW, full-bore revision (stunt-merge regression 2026-07-19). The arc radius
	/// R = (L²+H²)/(2H) is what the suspension feels: riding the face at speed v costs a sustained
	/// centripetal load v²/R on top of gravity, and a bottomed wheel's resolution impulse on a slope
	/// points backward: arrest + nose pitch + flip. The previous law R(H) = 14·H + 9 (floor 18) was
	/// verified at 21-30 m/s test entries, but the merged open map gives unlimited runup and the
	/// hatch reaches 46 m/s. MEASURED survival boundary (hatch, jump maneuver, entry = maxSpeedMs):
	///   R 37 / exit 19°: clean at 25.0 (33 G), marginal at 28.4 (52 G), FLIP+ARREST from 31.0
	///     (108 G) through 44.7 (233 G, the owner-reported wall-stop).
	///   R 72 / exit 20°: 44.8 entry, NO arrest, drives on; 70 G flat-landing slam.
	///   R 93 / exit 21°: 45.1 entry, NO arrest; 60 G flat-landing slam.
	///   R 87.5 / exit 7.9° and R 115 / exit 5.0° (proving ramps): 42+ entry, 3-5 G, pristine.
	/// Two separate mechanisms, two dials:
	///   1. FACE ARREST separates on R itself, not on v²/R alone (R 37 arrested at 26 m/s² while
	///      R 72 survived 28 m/s² at the same exit angle). Smallest radius proven safe at 45 m/s
	///      is 72; the floor is set at 90 (25% above it, face load 2.2 g at 46 m/s).
	///   2. LANDING SLAM scales with exit angle + lip height (landing vz 10.5 measured 33 G,
	///      11.3 measured 52 G, 18-20 measured 60-70 G). The slope 52·H caps every exit at ~11.3°,
	///      which holds full-bore landing vz near 11 m/s for the 2 m class. High speed does not
	///      need steep exits: a 3 m kicker at 11° still gives ~1.7 s of air at 40 m/s; trading
	///      exit angle for survivability is the design intent, stated here with the numbers.
	/// </summary>
	public const float MinRadiusM = 90f;

	/// <summary>Speed-safe ground run for a kicker of lip height <paramref name="heightM"/>: the length
	/// that yields the design radius R(H) = max(<see cref="MinRadiusM"/>, 52·H) (see the law above;
	/// increasing in H, exit angle capped ~11.3°). Never shorter than the legacy 5·H (inert with the
	/// 90 m floor, kept as a guard). From R = (L²+H²)/(2H):  L = √(H·(2R − H)).</summary>
	public static float LengthFor( float heightM )
	{
		float r = MathF.Max( MinRadiusM, 52f * heightM );
		float lawLen = MathF.Sqrt( heightM * (2f * r - heightM) );
		return MathF.Max( 5f * heightM, lawLen );
	}

	/// <summary>Place a curved solid kicker under <paramref name="parent"/>. <paramref name="lengthM"/>
	/// = ground run, <paramref name="widthM"/> = lateral width, <paramref name="heightM"/> = lip height.
	/// Returns the placed GameObject.</summary>
	public static GameObject Build( Scene scene, GameObject parent, Vector3 atM, float yawDeg,
		float lengthM, float widthM, float heightM, Color color,
		RampProfile profile = RampProfile.Arc, int segments = 16 )
	{
		Vector2[] prof;
		if ( profile == RampProfile.Easement )
		{
			// resolution targets ~EasementSegmentMeters of run per collider segment; the run is
			// ~length/(1 - blend/2), estimated here (exact run comes out of the integration)
			float approxRun = lengthM / (1f - EasementBlend * 0.5f);
			int esegs = System.Math.Clamp( (int)MathF.Ceiling( approxRun / EasementSegmentMeters ), 16, MaxSegments );
			prof = EasementCore( lengthM, heightM, esegs ).prof;
		}
		else
		{
			int segs = System.Math.Max( 3, segments );
			prof = Profile( lengthM, heightM, segs );
		}
		var model = BuildRenderModel( prof, widthM );

		var go = scene.CreateObject();
		go.Name = "Kicker";
		go.SetParent( parent, true );
		go.WorldPosition = atM * M;
		go.WorldRotation = Rotation.FromYaw( yawDeg );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = color;

		BuildSegmentColliders( go, prof, widthM );

		go.Tags.Add( "road" );   // wheels treat the curved face as drivable ground
		return go;
	}

	/// <summary>The clothoid-blended easement profile: curvature k(s) rises linearly from 0 to
	/// k_max = 1/R over the first <see cref="EasementBlend"/> of the arc length, then holds to the
	/// lip. R and the exit angle come from the authored (L,H) pair exactly as the arc law derives
	/// them, the total arc length is set so the final heading equals the arc's exit angle, and the
	/// integrated curve is uniformly scaled to land the lip at H exactly (uniform scaling
	/// preserves angles; the effective radius scales by the same ~0.96-0.99 factor, inside the
	/// law's 25% floor margin). Deterministic: fixed-step midpoint integration, no RNG.</summary>
	static (Vector2[] prof, float runM) EasementCore( float L, float H, int segs )
	{
		float R = (L * L + H * H) / (2f * H);
		float thetaExit = MathF.Asin( System.Math.Clamp( L / R, 0f, 1f ) );
		float S = thetaExit * R / (1f - EasementBlend * 0.5f);
		int n = segs * 64;                    // fine fixed-step integration, deterministic
		float ds = S / n;
		float sBlend = EasementBlend * S;
		var pts = new Vector2[segs + 1];
		pts[0] = Vector2.Zero;
		double x = 0, z = 0, theta = 0;
		int stride = n / segs;
		for ( int i = 1; i <= n; i++ )
		{
			float sMid = (i - 0.5f) * ds;
			float k = sMid < sBlend ? (sMid / sBlend) / R : 1f / R;
			double thMid = theta + k * ds * 0.5;   // heading at the step midpoint
			theta += k * ds;
			x += System.Math.Cos( thMid ) * ds;
			z += System.Math.Sin( thMid ) * ds;
			if ( i % stride == 0 )
				pts[i / stride] = new Vector2( (float)x, (float)z );
		}
		float scale = H / (float)z;           // land the lip exactly at H; angles preserved
		for ( int i = 0; i <= segs; i++ )
			pts[i] *= scale;
		return (pts, pts[segs].x);
	}

	/// <summary>The sampled arc profile (x, z) in metres, tangent-to-ground at x=0. Circle centre at
	/// (0,R), z(x) = R − √(R²−x²) with R = (L²+H²)/(2H) so z(L)=H exactly (slope 0 at the base).</summary>
	static Vector2[] Profile( float L, float H, int segs )
	{
		float R = (L * L + H * H) / (2f * H);
		var prof = new Vector2[segs + 1];
		for ( int i = 0; i <= segs; i++ )
		{
			float x = L * i / segs;
			float z = R - MathF.Sqrt( MathF.Max( 0f, R * R - x * x ) );
			prof[i] = new Vector2( x, z );
		}
		return prof;
	}

	// ---------------------------------------------------------------- collision (segmented convex boxes)

	/// <summary>One solid convex box per arc segment, pitched to the local slope, its top face lying on
	/// the segment chord and its body buried below grade so overlapping neighbours make a gapless solid
	/// whose only exposed surface is the faceted arc. This is the collider the wheels actually meet.</summary>
	static void BuildSegmentColliders( GameObject kicker, Vector2[] prof, float W )
	{
		int segs = prof.Length - 1;
		const float buryM = 1.5f;   // how far each box reaches below grade → thick, overlapping solid

		for ( int i = 0; i < segs; i++ )
		{
			var p0 = prof[i];
			var p1 = prof[i + 1];
			float dx = p1.x - p0.x;
			float dz = p1.y - p0.y;
			float segLen = MathF.Sqrt( dx * dx + dz * dz );
			if ( segLen < 1e-4f ) continue;

			float pitchDeg = MathF.Atan2( dz, dx ).RadianToDegree();  // segment slope above horizontal
			float midX = (p0.x + p1.x) * 0.5f;
			float midZ = (p0.y + p1.y) * 0.5f;

			// upward surface normal (perp to the tangent, points up-and-back): (-dz, dx)/segLen
			float nX = -dz / segLen;
			float nZ = dx / segLen;

			float boxThick = midZ + buryM;                 // surface → below grade
			float cX = midX - nX * boxThick * 0.5f;        // centre sits half a thickness below the surface
			float cZ = midZ - nZ * boxThick * 0.5f;

			var seg = kicker.Scene.CreateObject();
			seg.Name = "KickerSeg";
			seg.SetParent( kicker, false );                // keep parent yaw; add local pitch below
			seg.LocalPosition = new Vector3( cX, 0f, cZ ) * M;
			seg.LocalRotation = Rotation.FromPitch( -pitchDeg );  // +X points up the slope (nose-up)

			var box = seg.Components.Create<BoxCollider>();
			box.Scale = new Vector3( segLen, W, boxThick ) * M;   // full box size in units
			box.Static = true;
			seg.Tags.Add( "road" );
		}
	}

	// ---------------------------------------------------------------- render mesh (visual only)

	/// <summary>Mesh the closed curved-kicker solid for RENDER (no collision — see
	/// <see cref="BuildSegmentColliders"/>). Top curved surface, two side skirts down to grade, a flat
	/// underside and the vertical back lip, wound so every face points outward via the self-check.</summary>
	static Model BuildRenderModel( Vector2[] prof, float W )
	{
		int segs = prof.Length - 1;
		float hw = W * 0.5f;
		float L = prof[segs].x;
		float H = prof[segs].y;

		var verts = new List<Vertex>();
		var positions = new List<Vector3>();
		var indices = new List<int>();

		int AddVert( Vector3 pMeters, Vector3 normal )
		{
			var p = pMeters * M;
			positions.Add( p );
			verts.Add( new Vertex( p, normal, new Vector3( 1f, 0f, 0f ), new Vector4( pMeters.x * 0.1f, pMeters.y * 0.1f, 0f, 0f ) ) );
			return verts.Count - 1;
		}

		// Emit a quad A-B-C-D, picking the winding so the front face points along outN (keeps the
		// single-sided default material facing out). Same self-checking trick avoids the terrain
		// "face-down winding" trap by construction.
		void Quad( Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 outN )
		{
			bool flip = Vector3.Dot( Vector3.Cross( B - A, C - A ), outN ) < 0f;
			int a = AddVert( A, outN ), b = AddVert( B, outN ), c = AddVert( C, outN ), d = AddVert( D, outN );
			if ( !flip ) { indices.Add( a ); indices.Add( b ); indices.Add( c ); indices.Add( a ); indices.Add( c ); indices.Add( d ); }
			else { indices.Add( a ); indices.Add( c ); indices.Add( b ); indices.Add( a ); indices.Add( d ); indices.Add( c ); }
		}

		for ( int i = 0; i < segs; i++ )
		{
			var p0 = prof[i]; var p1 = prof[i + 1];
			// per-segment up-normal from the arc tangent (leans back as it steepens): (-dz,0,dx)
			var up = new Vector3( -(p1.y - p0.y), 0f, p1.x - p0.x ).Normal;
			// TOP (drivable curved surface)
			Quad( new Vector3( p0.x, -hw, p0.y ), new Vector3( p1.x, -hw, p1.y ),
				new Vector3( p1.x, hw, p1.y ), new Vector3( p0.x, hw, p0.y ), up );
			// LEFT skirt (y = -hw) and RIGHT skirt (y = +hw) — fill under the curve down to grade
			Quad( new Vector3( p0.x, -hw, 0f ), new Vector3( p1.x, -hw, 0f ),
				new Vector3( p1.x, -hw, p1.y ), new Vector3( p0.x, -hw, p0.y ), new Vector3( 0f, -1f, 0f ) );
			Quad( new Vector3( p0.x, hw, 0f ), new Vector3( p1.x, hw, 0f ),
				new Vector3( p1.x, hw, p1.y ), new Vector3( p0.x, hw, p0.y ), new Vector3( 0f, 1f, 0f ) );
		}

		// UNDERSIDE (flat on grade) and BACK LIP (vertical face at x=L) — seal the solid
		Quad( new Vector3( 0f, -hw, 0f ), new Vector3( L, -hw, 0f ),
			new Vector3( L, hw, 0f ), new Vector3( 0f, hw, 0f ), new Vector3( 0f, 0f, -1f ) );
		Quad( new Vector3( L, -hw, 0f ), new Vector3( L, hw, 0f ),
			new Vector3( L, hw, H ), new Vector3( L, -hw, H ), new Vector3( 1f, 0f, 0f ) );

		var idx = new int[indices.Count];
		for ( int k = 0; k < idx.Length; k++ ) idx[k] = indices[k];

		var mesh = new Mesh( Material.Load( "materials/default.vmat" ) );
		mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( idx.Length, idx );
		mesh.Bounds = BBox.FromPoints( positions );

		return Model.Builder.AddMesh( mesh ).Create();
	}
}
