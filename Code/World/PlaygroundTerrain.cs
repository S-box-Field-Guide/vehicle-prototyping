namespace VehicleProto;

/// <summary>
/// Playground terrain — a STANDALONE gentle heightfield written
/// fresh in this repo so "driving isn't a flat plane", deliberately highway-grade (amplitude ≤ ~3 m,
/// wavelengths ≥ ~60 m — undulation, not offroad). A plain analytic
/// sin-blend + hash-noise ribbon meshed once with <see cref="Model.Builder"/> (AddMesh render +
/// AddCollisionMesh physics) — deliberately simple, not a chunked voxel terrain system.
///
/// Determinism law: height is a pure function of world XY + a fixed seed constant (hash noise only,
/// no System.Random), so the same playground meshes byte-identically every boot.
///
/// FLAT CORE: everything the playground BUILDS (arterial + connectors, the buildings, the loop
/// track, the banked bowl) sits inside a central pad that is stamped perfectly flat (height 0) with a
/// smooth cosine shoulder out to the hills — so no structure ever lands on a slope and the raycast
/// wheels only meet the gentle undulation out in the open exploration ring. If the wheels misbehave
/// on the collision even so (the KB coarse-collision traps), the §4 TRIPWIRE drops the whole world
/// back to the flat plane (Option A) — the accepted fallback.
/// </summary>
public static class PlaygroundTerrain
{
	const float M = Units.MetersToUnits;

	// gentle-undulation dials (metres). Highway-grade: low amplitude, long wavelengths.
	const int Seed = 20260713;
	const float Amplitude = 2.6f;     // ≤ ~3 m peak
	const float Wave1 = 82f;          // ≥ ~60 m primary wavelength
	const float Wave2 = 133f;         // secondary, non-harmonic for organic blend
	const float NoiseWave = 61f;      // hash-noise cell size

	// flat core: covers the whole structured playground (buildings ±150, loop ±150) with a shoulder.
	const float CoreHalf = 172f;      // m — inside this the ground is dead flat (height 0)
	const float Shoulder = 46f;       // m — cosine ramp from flat core up into the hills

	static readonly Color TerrainTan = new( 0.60f, 0.56f, 0.46f );

	/// <summary>Build the heightfield ground under <paramref name="root"/>. <paramref name="halfM"/>
	/// is the ground half-extent (matches the flat pad it replaces). Cell ~8 m keeps the mesh cheap
	/// (~65² verts) while staying smooth at highway wavelengths.</summary>
	public static void Build( Scene scene, GameObject root, float halfM )
	{
		const float cellM = 8f;
		int n = (int)(halfM * 2f / cellM) + 1;     // verts per side
		float step = halfM * 2f / (n - 1);

		var verts = new List<Vertex>( n * n );
		var positions = new List<Vector3>( n * n );
		for ( int j = 0; j < n; j++ )
		for ( int i = 0; i < n; i++ )
		{
			float x = -halfM + i * step;
			float y = -halfM + j * step;
			float h = HeightM( x, y );
			var p = new Vector3( x, y, h ) * M;
			positions.Add( p );
			// finite-difference normal + tangent from the analytic height field (audit 2026-07-13 LOW —
			// was a flat Vector3.Up, so the hills lit flat and read as a shading discontinuity against
			// other geometry). Central difference in metres over a fixed 1 m epsilon; slopes are gentle
			// so the height field is smooth at this step. z-up convention: n = (-dz/dx, -dz/dy, 1).
			const float e = 1f;
			float dzdx = (HeightM( x + e, y ) - HeightM( x - e, y )) / (2f * e);
			float dzdy = (HeightM( x, y + e ) - HeightM( x, y - e )) / (2f * e);
			var normal = new Vector3( -dzdx, -dzdy, 1f ).Normal;
			var tangent = new Vector3( 1f, 0f, dzdx ).Normal; // along +X, following the surface slope
			verts.Add( new Vertex( p, normal, tangent, new Vector4( x * 0.1f, y * 0.1f, 0, 0 ) ) );
		}

		// two triangles per cell (consistent diagonal — avoids the KB sawtooth-diagonal artifact).
		// WINDING: for a=origin, b=a+X, c=a+Y, d=a+X+Y the front-facing pattern is {a,b,c},{b,d,c}
		// (plain right-hand CCW, front = X×Y = +Z UP). The reversed {a,c,b},{b,c,d} renders the
		// ground FACE-DOWN under the single-sided materials/default.vmat: backface-culled from every
		// above-view, visible only from below (verified in-editor here 2026-07-13 via above/below
		// screenshots — the exact "hole beside the road + floating distant patches" symptom). The
		// "{a,c,b} is up-facing, do NOT fix" lore is NOT portable and is WRONG for single-sided
		// materials; the up-facing winding was verified directly in-editor (see above).
		var indices = new List<int>( (n - 1) * (n - 1) * 6 );
		for ( int j = 0; j < n - 1; j++ )
		for ( int i = 0; i < n - 1; i++ )
		{
			int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
			indices.Add( a ); indices.Add( b ); indices.Add( c );
			indices.Add( b ); indices.Add( d ); indices.Add( c );
		}

		var idxArr = new int[indices.Count];
		for ( int k = 0; k < indices.Count; k++ ) idxArr[k] = indices[k];   // no Array.Clone (whitelist)

		var mat = Material.Load( "materials/default.vmat" );
		var mesh = new Mesh( mat );
		mesh.CreateVertexBuffer( verts.Count, verts );
		mesh.CreateIndexBuffer( idxArr.Length, idxArr );
		mesh.Bounds = BBox.FromPoints( positions );

		var model = Model.Builder
			.AddMesh( mesh )
			.AddCollisionMesh( ToUnitPositions( positions ), idxArr )   // physics from the SAME grid
			.Create();

		var go = scene.CreateObject();
		go.Name = "Terrain";
		go.SetParent( root, true );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = TerrainTan;

		var collider = go.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
		go.Tags.Add( "road" );

		Log.Info( $"[vp] terrain built: {n}x{n} heightfield, amp {Amplitude} m, flat core ±{CoreHalf} m" );
	}

	/// <summary>Height (metres) at a world XY. Flat (0) inside the core, a smooth cosine shoulder up
	/// into gentle rolling hills beyond. Pure + deterministic (hash noise, no RNG).</summary>
	static float HeightM( float x, float y )
	{
		// distance into the hill zone: 0 at/inside the core edge, 1 once fully past the shoulder.
		float d = MathF.Max( MathF.Abs( x ), MathF.Abs( y ) ) - CoreHalf;
		if ( d <= 0f )
			return 0f;
		float ramp = d >= Shoulder ? 1f : 0.5f - 0.5f * MathF.Cos( d / Shoulder * MathF.PI ); // smoothstep-ish

		// gentle non-harmonic undulation + a little hash noise
		float s = MathF.Sin( x / Wave1 * MathF.PI * 2f ) * MathF.Cos( y / Wave2 * MathF.PI * 2f )
			+ 0.5f * MathF.Sin( (x + y) / Wave2 * MathF.PI * 2f );
		float noise = HashNoise( x / NoiseWave, y / NoiseWave ) - 0.5f;
		return ramp * Amplitude * (0.7f * s + 0.6f * noise);
	}

	/// <summary>Deterministic value-noise (bilinear over a hashed integer lattice), range ~[0,1].</summary>
	static float HashNoise( float fx, float fy )
	{
		int x0 = (int)MathF.Floor( fx ), y0 = (int)MathF.Floor( fy );
		float tx = fx - x0, ty = fy - y0;
		float h00 = Hash01( x0, y0 ), h10 = Hash01( x0 + 1, y0 );
		float h01 = Hash01( x0, y0 + 1 ), h11 = Hash01( x0 + 1, y0 + 1 );
		float sx = tx * tx * (3f - 2f * tx), sy = ty * ty * (3f - 2f * ty);
		return h00 + (h10 - h00) * sx + (h01 - h00) * sy + (h00 - h10 - h01 + h11) * sx * sy;
	}

	static float Hash01( int x, int y )
	{
		// FNV-ish integer hash → [0,1). No System.Random (determinism law).
		uint h = (uint)Seed;
		h = (h ^ (uint)x) * 2654435761u;
		h = (h ^ (uint)y) * 2246822519u;
		h ^= h >> 15;
		return (h & 0xFFFFFF) / (float)0x1000000;
	}

	static Vector3[] ToUnitPositions( List<Vector3> unitPositions )
	{
		var a = new Vector3[unitPositions.Count];
		for ( int i = 0; i < a.Length; i++ ) a[i] = unitPositions[i];   // already in units
		return a;
	}
}
