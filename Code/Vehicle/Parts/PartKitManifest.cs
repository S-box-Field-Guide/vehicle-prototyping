namespace VehicleProto;

/// <summary>
/// DTOs + loader for a part-kit <c>manifest.json</c> (schema <c>vp.partkit/1</c>, the
/// Stage-A contract authored by <c>tools/gen_vehicle.py</c> — see docs/part-kit-pipeline.md).
///
/// Property names deliberately match the JSON keys byte-for-byte (snake_case) so no
/// serializer attributes are needed: <c>Sandbox.Json</c> is System.Text.Json underneath,
/// which matches case-sensitively and silently ignores JSON fields with no member (that is
/// how <c>spec_m</c>/<c>frames</c>/etc. are skipped — we only bind what assembly consumes).
/// Whitelist status: <c>Json.Deserialize</c> + <c>FileSystem</c> are the proven house save/load
/// APIs; reading a loose .json from
/// <c>FileSystem.Mounted</c> is verified live in-editor (ship-time packaging of loose
/// json is a later concern, noted in docs/part-kit-assembly.md).
/// </summary>
public sealed class PartKitManifest
{
	public string schema { get; set; }
	public string kit { get; set; }
	public List<PartKitPart> parts { get; set; }

	/// <summary>OPTIONAL additive field (no schema bump, same law as <c>required</c>):
	/// the citizen sit point in the AUTHORING frame, metres (de-Kenney kart, 2026-07-13 —
	/// the driver is always the engine citizen via VehicleFactory.AddDriver,
	/// never a modeled figure). Consumed through <see cref="DriverLocalM"/>.</summary>
	public float[] driver_seat_author_m { get; set; }

	/// <summary>v1 = pre-fix generator emission: frame fields computed with the DISPROVEN
	/// mapping (local_bounds_* 180°-yawed, attach_local_m mirrored). Normalized at load.</summary>
	public const string SchemaV1 = "vp.partkit/1";

	/// <summary>v2 = generator emission fixed to the PROVEN mapping (2026-07-12):
	/// local_bounds_* and attach_local_m are true model-local values, consumed as-is.</summary>
	public const string SchemaV2 = "vp.partkit/2";

	/// <summary>v3 = adds the destruction damage band (D1, 2026-07-12): per-part
	/// <c>dent_impulse</c>/<c>loosen_impulse</c>/<c>detach_impulse</c>/<c>stiffness</c>/<c>max_crush_m</c>
	/// + a <c>zone</c> tag. Frame handling is IDENTICAL to v2 (proven mapping, consumed
	/// as-is). Per the versioning law, <see cref="TryLoad"/> NORMALIZES older manifests up at load —
	/// v1 gets the bounds flip AND damage defaults, v2 gets damage defaults — so a consumer never
	/// compensates for a missing field regardless of the on-disk schema.</summary>
	public const string SchemaV3 = "vp.partkit/3";

	/// <summary>Recognized <c>zone</c> tags (the systems a crumple would degrade).
	/// front = front-corner steering coupling, hood = torque coupling, rear/door/cabin = cosmetic or
	/// reserved, wheel = never-dent (physics contract).</summary>
	public static readonly HashSet<string> RecognizedZones = new()
	{ "front", "hood", "rear", "door", "cabin", "wheel" };

	/// <summary>
	/// FRAME CONTRACT (empirically resolved 2026-07-11, kit assembly — full derivation +
	/// screenshot evidence in docs/part-kit-assembly.md §2):
	///
	///   authoring (Blender): +X fwd, +Y left, +Z up, metres
	///   OBJ file  (exporter forward=-Z/up=Y):  o = ( bX,  bZ, -bY )  [disk: door-handle probe]
	///   s&amp;box OBJ import:                      m = ( oZ,  oX,  oY )  [live: facing screenshots —
	///        the plain Y-up→Z-up CYCLIC PERMUTATION, no sign flips; NOT a negated formula]
	///   => author -> model-local:              m = (-bY,  bX,  bZ )
	///
	/// Model-local frame is therefore +X = vehicle RIGHT, +Y = vehicle FRONT, +Z = up
	/// (nose points local +Y; FromYaw(-90) on the kit-body GO faces the car down root +X).
	///
	/// SCHEMA HISTORY: v1 manifests (the landed hatch_kit) carry frame fields computed with
	/// the DISPROVEN mapping — <c>attach_local_m</c>/<c>frames.author_to_local</c> claim
	/// (-bY,-bX,bZ) (determinant -1, a mirror this proper-rotation pipeline cannot produce)
	/// and <c>local_bounds_*</c> are exactly 180°-yawed from truth. <see cref="TryLoad"/>
	/// normalizes v1 bounds to the true frame at load (negate x AND y of min/max, swapped);
	/// v2 manifests (gen_vehicle.py after the v2 emission fix) are already true and load as-is.
	/// <c>attach_author_m</c> is frame-agnostic authoring truth on BOTH schemas and remains
	/// the position source we consume.
	/// First landing used the older negated import formula: every part's PIVOT POSITION came
	/// out world-correct but every MESH sat 180°-spun in place (taillights facing +X) — the
	/// two errors cancel in position and add in orientation. The conversion + yaw constants
	/// here must therefore always flip TOGETHER.
	/// </summary>
	public static Vector3 AuthorToLocal( float[] b ) => new( -b[1], b[0], b[2] );

	/// <summary>Facing convention: model nose points local +Y (verified live); yaw -90° on the
	/// kit-body GO aims it down the vehicle root's +X (forward).</summary>
	public static readonly Rotation ModelToRootYaw = Rotation.FromYaw( -90f );

	/// <summary>The four wheel part names the assembler indexes by name
	/// (<see cref="PartKitAssembler.MountWheelVisual"/>). Validation requires exactly one of each.</summary>
	public static readonly string[] WheelNames = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };

	/// <summary>Recognized <c>kind</c> tags (Stage-C semantic tags). An unrecognized kind is a
	/// hard validation error — later passes (hinge motion, break-off) switch on these.</summary>
	static readonly HashSet<string> RecognizedKinds = new()
	{ "chassis", "wheel", "door", "hood", "trunk", "tailgate", "bed", "bolton", "fascia", "mirror", "accessory" };

	/// <summary>Kinds whose model is COSMETIC and therefore OPTIONAL by default: a failed load
	/// is skipped, not a kit-wide fallback. A part may override either way with explicit
	/// <c>"required": true/false</c> in the manifest. See docs/part-kit-assembly.md (hardening).</summary>
	public static readonly HashSet<string> OptionalByDefaultKinds = new() { "mirror", "accessory" };

	/// <summary>Recognized values for <c>rotation_axis_local</c> (null = no hinge axis).</summary>
	static readonly HashSet<string> RecognizedAxes = new() { "X", "Y", "Z" };

	/// <summary>
	/// STRICT structural validation of every field the assembler will index. Collects ALL errors
	/// (each tagged with kit + part name) so one pass surfaces every authoring/generator mistake.
	/// Returns true only when the manifest is safe to assemble. Runs on RAW (pre-normalization)
	/// values — bounds ordering is frame-preserving, so raw min≤max ⟺ normalized min≤max (the v1
	/// normalization negates-and-swaps x,y, which preserves ordering; z is untouched).
	///
	/// LOCKSTEP: these rules are mirrored, rule-for-rule, in tools/test_partkit.py's
	/// validate_manifest(). Change one, change the other (the C# side is the enforcement; the
	/// python side guards the tools/gen_vehicle.py generator contract offline).
	/// </summary>
	public bool Validate( out List<string> errors )
	{
		errors = new List<string>();

		if ( schema != SchemaV1 && schema != SchemaV2 && schema != SchemaV3 )
			errors.Add( $"kit '{kit}': schema '{schema}' not in ('{SchemaV1}', '{SchemaV2}', '{SchemaV3}')" );

		if ( string.IsNullOrWhiteSpace( kit ) )
			errors.Add( "manifest: 'kit' name is empty" );

		// optional citizen sit point: when present it must be finite-3 (LOCKSTEP:
		// mirrored in tools/test_partkit.py + fixtures/bad_driver_seat.json)
		if ( driver_seat_author_m != null && !Finite3( driver_seat_author_m ) )
			errors.Add( $"kit '{kit}': driver_seat_author_m, if present, must be a finite length-3 array" );

		if ( parts is null || parts.Count == 0 )
		{
			errors.Add( $"kit '{kit}': no parts" );
			return false; // nothing more can be checked
		}

		var seen = new HashSet<string>();
		for ( int i = 0; i < parts.Count; i++ )
		{
			var p = parts[i];
			if ( p is null )
			{
				errors.Add( $"kit '{kit}': part [index {i}] is null" );
				continue;
			}

			string id = string.IsNullOrWhiteSpace( p.part ) ? $"[index {i}]" : $"'{p.part}'";
			if ( string.IsNullOrWhiteSpace( p.part ) )
				errors.Add( $"kit '{kit}': part [index {i}] has an empty 'part' name" );
			else if ( !seen.Add( p.part ) )
				errors.Add( $"kit '{kit}': duplicate part name '{p.part}'" );

			ValidatePart( kit, p, id, errors );
		}

		// exactly one FL/FR/RL/RR wheel set (assembler looks each up by name)
		foreach ( var wn in WheelNames )
		{
			int c = parts.Count( p => p?.part == wn );
			if ( c != 1 )
				errors.Add( $"kit '{kit}': expected exactly one '{wn}', found {c}" );
		}
		// no stray kind=="wheel" parts outside the canonical set
		foreach ( var p in parts )
			if ( p?.kind == "wheel" && p.part != null && !WheelNames.Contains( p.part ) )
				errors.Add( $"kit '{kit}': part '{p.part}' has kind 'wheel' but is not one of the FL/FR/RL/RR set" );

		// at least one body (non-wheel) part — a wheels-only kit has no drivable body
		if ( !parts.Any( p => p != null && p.kind != "wheel" ) )
			errors.Add( $"kit '{kit}': has no body (non-wheel) parts" );

		return errors.Count == 0;
	}

	static void ValidatePart( string kit, PartKitPart p, string id, List<string> errors )
	{
		void Bad( string msg ) => errors.Add( $"kit '{kit}' part {id}: {msg}" );

		if ( string.IsNullOrWhiteSpace( p.vmdl ) )
			Bad( "empty 'vmdl'" );
		if ( string.IsNullOrWhiteSpace( p.kind ) )
			Bad( "empty 'kind'" );
		else if ( !RecognizedKinds.Contains( p.kind ) )
			Bad( $"unrecognized kind '{p.kind}' (allowed: {string.Join( ", ", RecognizedKinds )})" );

		if ( p.rotation_axis_local != null && !RecognizedAxes.Contains( p.rotation_axis_local ) )
			Bad( $"unrecognized rotation_axis_local '{p.rotation_axis_local}' (allowed: null, X, Y, Z)" );

		// dims: present, length 3, finite, strictly positive (they scale the BoxCollider)
		if ( !Finite3( p.dims_m ) )
			Bad( "dims_m must be a finite length-3 array" );
		else if ( p.dims_m[0] <= 0f || p.dims_m[1] <= 0f || p.dims_m[2] <= 0f )
			Bad( $"dims_m must be positive, got [{p.dims_m[0]},{p.dims_m[1]},{p.dims_m[2]}]" );

		// bounds: present, length 3, finite, ordered (min ≤ max per axis)
		bool loOk = Finite3( p.local_bounds_min_m ), hiOk = Finite3( p.local_bounds_max_m );
		if ( !loOk ) Bad( "local_bounds_min_m must be a finite length-3 array" );
		if ( !hiOk ) Bad( "local_bounds_max_m must be a finite length-3 array" );
		if ( loOk && hiOk )
			for ( int a = 0; a < 3; a++ )
				if ( p.local_bounds_min_m[a] > p.local_bounds_max_m[a] )
					Bad( $"local_bounds min > max on axis {a} ({p.local_bounds_min_m[a]} > {p.local_bounds_max_m[a]})" );

		// attach_author_m is the CONSUMED position source (AuthorToLocal / AttachRootM)
		if ( !Finite3( p.attach_author_m ) )
			Bad( "attach_author_m must be a finite length-3 array" );

		// attach_local_m is bound-but-not-consumed; only shape-check it when present
		if ( p.attach_local_m != null && !Finite3( p.attach_local_m ) )
			Bad( "attach_local_m, if present, must be a finite length-3 array" );

		if ( !float.IsFinite( p.mass_fraction ) || p.mass_fraction < 0f )
			Bad( $"mass_fraction must be finite and non-negative, got {p.mass_fraction}" );

		// ── schema v3 damage band — validated WHEN PRESENT (absent = loader fills a kind default,
		// per the versioning law; so v1/v2 manifests that omit the whole band stay valid). All fields
		// are finite and non-negative; max_crush_m is legitimately 0 for wheels (never-dent sentinel). ──
		void DamageField( string name, float? v )
		{
			if ( v is null ) return;
			if ( !float.IsFinite( v.Value ) || v.Value < 0f )
				Bad( $"{name} must be finite and non-negative, got {v.Value}" );
		}
		DamageField( "dent_impulse", p.dent_impulse );
		DamageField( "loosen_impulse", p.loosen_impulse );
		DamageField( "detach_impulse", p.detach_impulse );
		DamageField( "stiffness", p.stiffness );
		DamageField( "max_crush_m", p.max_crush_m );

		// severity band must be ordered when all three thresholds are present: a part cannot loosen
		// before it dents, nor detach before it loosens.
		if ( p.dent_impulse is float di && p.loosen_impulse is float li && di > li )
			Bad( $"dent_impulse ({di}) must be <= loosen_impulse ({li})" );
		if ( p.loosen_impulse is float li2 && p.detach_impulse is float de && li2 > de )
			Bad( $"loosen_impulse ({li2}) must be <= detach_impulse ({de})" );

		if ( p.zone != null && !RecognizedZones.Contains( p.zone ) )
			Bad( $"unrecognized zone '{p.zone}' (allowed: null, {string.Join( ", ", RecognizedZones )})" );
	}

	static bool Finite3( float[] a )
		=> a != null && a.Length == 3 && float.IsFinite( a[0] ) && float.IsFinite( a[1] ) && float.IsFinite( a[2] );

	/// <summary>Load + STRICTLY validate a manifest from the mounted filesystem (path relative to
	/// Assets/, e.g. "models/vehicles/hatch_kit/manifest.json"). Null on any failure — callers
	/// fall back to the blockout body path so a bad kit never bricks a spawn. Guaranteed
	/// un-throwable for any parseable JSON: a parse/bind error, a null result, or ANY validation
	/// failure all return null with a diagnostic instead of surfacing an exception at spawn.</summary>
	public static PartKitManifest TryLoad( string path )
	{
		if ( string.IsNullOrEmpty( path ) )
			return null;

		try
		{
			if ( !FileSystem.Mounted.FileExists( path ) )
			{
				Log.Warning( $"[vp] partkit manifest not found in mounted fs: '{path}'" );
				return null;
			}

			return FromJson( FileSystem.Mounted.ReadAllText( path ), path, out _ );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[vp] partkit manifest '{path}' failed to load/parse: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// The REAL parse → strict-validate → schema up-normalize core, shared by <see cref="TryLoad"/>
	/// (file path) and the editor <c>vp_validate_manifest</c> test tool (raw JSON text). Returns null
	/// with <paramref name="reasons"/> filled on ANY parse/null/validation failure, a fully-normalized
	/// manifest otherwise. Un-throwable for any input. <paramref name="label"/> only tags the logs.
	/// Extracting this lets the offline Python fixture battery run its 32 cases through the ACTUAL C#
	/// loader rules (audit 2026-07-13 MEDIUM — Python/C# validator drift is otherwise undetectable).
	/// </summary>
	public static PartKitManifest FromJson( string text, string label, out List<string> reasons )
	{
		reasons = new List<string>();

		PartKitManifest manifest;
		try
		{
			manifest = Json.Deserialize<PartKitManifest>( text );
		}
		catch ( System.Exception e )
		{
			// malformed JSON, wrong field types (e.g. dims_m: "x"), NaN/Infinity literals, or an STJ
			// property-name collision — System.Text.Json throws; swallow it into the fallback contract.
			reasons.Add( $"not valid/bindable JSON: {e.Message}" );
			Log.Warning( $"[vp] partkit manifest '{label}' is not valid/bindable JSON: {e.Message}" );
			return null;
		}

		if ( manifest is null )
		{
			reasons.Add( "deserialized to null" );
			Log.Warning( $"[vp] partkit manifest '{label}' deserialized to null — ignoring kit" );
			return null;
		}

		// FULL validation BEFORE any consumer indexes a field. A parseable-but-broken manifest
		// is rejected HERE (→ null → blockout) rather than throwing mid-spawn.
		if ( !manifest.Validate( out var errors ) )
		{
			reasons.AddRange( errors );
			Log.Warning( $"[vp] partkit manifest '{label}' rejected ({errors.Count} error(s)) — falling back to blockout:" );
			foreach ( var e in errors )
				Log.Warning( $"[vp]   - {e}" );
			return null;
		}

		// v1 bounds were computed with the old negated import formula — exactly 180°-yawed
		// from engine truth (verified live in-editor). Normalize ONCE here so every downstream
		// consumer sees true model-local bounds regardless of schema:
		//   trueMin.xy = -recordedMax.xy, trueMax.xy = -recordedMin.xy, z unchanged.
		// Arrays are guaranteed present/length-3 by Validate above.
		if ( manifest.schema == SchemaV1 )
		{
			foreach ( var p in manifest.parts )
			{
				var lo = p.local_bounds_min_m;
				var hi = p.local_bounds_max_m;
				p.local_bounds_min_m = new[] { -hi[0], -hi[1], lo[2] };
				p.local_bounds_max_m = new[] { -lo[0], -lo[1], hi[2] };
			}
		}

		// SCHEMA v3 UP-NORMALIZATION (versioning law — never a compensating consumer): any damage
		// field a manifest omits (all of v1/v2, or a partial v3) is filled HERE with the kind/dims
		// default so the impact model always reads a populated band. Present values are kept.
		foreach ( var p in manifest.parts )
			p.ApplyDamageDefaults();

		return manifest;
	}

	public PartKitPart Find( string name )
		=> parts?.FirstOrDefault( p => p.part == name );

	/// <summary>The legacy fixed driver offset (root frame, metres) used by the blockout
	/// paths from the start — kept as the kit-path default when a manifest carries no sit point.</summary>
	public static readonly Vector3 DefaultDriverLocalM = new( 0.05f, 0f, 0.06f );

	/// <summary>Citizen driver GO position in the vehicle ROOT frame, metres. The authoring
	/// frame shares the root frame's axes (+X fwd, +Y left, +Z up) but its ground sits
	/// <paramref name="seatHeightM"/> below the root origin, so only z shifts. Falls back to
	/// <see cref="DefaultDriverLocalM"/> when the manifest has no sit point.</summary>
	public Vector3 DriverLocalM( float seatHeightM )
		=> driver_seat_author_m is { Length: 3 } d
			? new Vector3( d[0], d[1], d[2] - seatHeightM )
			: DefaultDriverLocalM;
}

/// <summary>One <c>parts[]</c> entry. Field semantics per docs/part-kit-pipeline.md:
/// pivots sit at the part's joint (hub centre / hinge line / mount plane), dims + bounds are
/// model-local metres relative to that pivot. <c>mass_fraction</c> is deliberately UNUSED in
/// Stage A (single-rigidbody core); it becomes real when Stage C gives detached
/// parts their own bodies.</summary>
public sealed class PartKitPart
{
	public string part { get; set; }
	public string vmdl { get; set; }
	public string kind { get; set; }
	public string pivot_semantics { get; set; }
	public string rotation_axis_local { get; set; }
	public float[] dims_m { get; set; }
	public float[] local_bounds_min_m { get; set; }
	public float[] local_bounds_max_m { get; set; }
	public float[] attach_local_m { get; set; }   // bound but NOT consumed (mirrored in v1; AuthorToLocal(attach_author_m) is equal on v2)
	public float[] attach_author_m { get; set; }  // the attach truth we consume (frame-agnostic, both schemas)
	public float mass_fraction { get; set; }
	public bool steer { get; set; }
	public bool mirror { get; set; }
	public int open_sign { get; set; }
	public string mount_normal { get; set; }

	// ── schema v3 damage band. Nullable so the loader can tell "omitted" (fill a kind
	// default) from an explicit value. After PartKitManifest.TryLoad they are guaranteed non-null;
	// the non-nullable accessors below are what the impact model reads. ──
	public float? dent_impulse { get; set; }
	public float? loosen_impulse { get; set; }
	public float? detach_impulse { get; set; }
	public float? stiffness { get; set; }
	public float? max_crush_m { get; set; }
	public string zone { get; set; }

	/// <summary>OPTIONAL additive field (both schemas — its absence does not change the format,
	/// so no schema bump). Explicitly marks whether a missing/errored model aborts the whole kit
	/// (true) or is skipped as cosmetic (false). When absent (null), the default falls out of the
	/// part's <c>kind</c>: cosmetic kinds (see <see cref="PartKitManifest.OptionalByDefaultKinds"/>
	/// — mirror/accessory) default optional, everything structural defaults required.</summary>
	public bool? required { get; set; }

	/// <summary>OPTIONAL additive field (both schemas, no schema bump — same law as
	/// <c>required</c>). Per-submesh index counts in <c>usemtl</c> / <see cref="Model.Materials"/>
	/// order (the s&amp;box compiler groups the single compiled mesh's index buffer into draw-ranges
	/// by material, in that order — EMPIRICALLY PROVEN 2026-07-13). A per-submesh mesh rebuilder (as used by a runtime
	/// deformation system, which is out of scope for this kit) can use it to split a dent rebuild per submesh so a
	/// multi-material part keeps every material,
	/// instead of flattening to <c>Materials[0]</c> and painting the whole part that one colour
	/// (the "dented pickup cab renders near-black" bug — its first usemtl is dark <c>trim</c>).
	/// Consumed only as a runtime-guarded hint (sum must equal the flattened index count); a
	/// missing/mismatched value just falls back to the single-material rebuild, so it is not part of
	/// manifest <c>Validate</c>.</summary>
	public int[] submesh_index_counts { get; set; }

	/// <summary>Whether a failed model load for this body part aborts the whole kit
	/// (→ blockout fallback) rather than being skipped. Explicit <c>required</c> wins; otherwise the
	/// kind decides. Wheels are handled separately (their mesh is cosmetic, physics is raycast).</summary>
	public bool IsRequired => required ?? !PartKitManifest.OptionalByDefaultKinds.Contains( kind ?? "" );

	/// <summary>Pivot position in chassis model-local metres (the frame under the kit-body GO).</summary>
	public Vector3 AttachLocalM => PartKitManifest.AuthorToLocal( attach_author_m );

	/// <summary>Pivot position in the vehicle ROOT frame's axes, metres (authoring frame and the
	/// root frame share axes: +X fwd, +Y left, +Z up — used by the wheel-alignment audit).</summary>
	public Vector3 AttachRootM => new( attach_author_m[0], attach_author_m[1], attach_author_m[2] );

	public Vector3 DimsM => new( dims_m[0], dims_m[1], dims_m[2] );

	/// <summary>Part bounds centre in TRUE model-local metres. Schema differences are already
	/// resolved by <see cref="PartKitManifest.TryLoad"/> (v1 bounds get the 180°-yaw
	/// normalization at load), so this is a plain centre on both schemas.</summary>
	public Vector3 BoundsCenterM => new(
		(local_bounds_min_m[0] + local_bounds_max_m[0]) * 0.5f,
		(local_bounds_min_m[1] + local_bounds_max_m[1]) * 0.5f,
		(local_bounds_min_m[2] + local_bounds_max_m[2]) * 0.5f );

	// ── schema v3 damage-band accessors: guaranteed non-null after PartKitManifest.TryLoad
	// (ApplyDamageDefaults fills any omitted field). The impact model reads THESE, never the raw
	// nullable JSON fields. ──
	// [JsonIgnore] IS REQUIRED (D1 live fix, 2026-07-13): Sandbox's Json.Deserialize runs
	// System.Text.Json with PropertyNameCaseInsensitive=true, so a computed getter-only accessor whose
	// name case-collides with its snake_case JSON field (`Stiffness`↔`stiffness`, `Zone`↔`zone`) makes
	// STJ throw "The JSON property name for 'X' collides with another property" while BUILDING the type
	// contract — bricking the WHOLE manifest load (kit silently fell back to blockout, no
	// ImpactRouter). Offline python validation can't catch this (no STJ binding). Ignoring the computed
	// accessors (they are code-only, never serialized) removes them from the contract. Kept on the whole
	// band group for consistency so a future accessor can't re-trip it.
	/// <summary>Impulse (N·s) at which this part STARTS denting. Sub-threshold hits do nothing.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public float DentImpulse => dent_impulse ?? DamageDefaults.For( kind, part, DimsM ).dent;
	/// <summary>Impulse (N·s) at which an attachment LOOSENS (visual wobble).</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public float LoosenImpulse => loosen_impulse ?? DamageDefaults.For( kind, part, DimsM ).loosen;
	/// <summary>Impulse (N·s) at which a DETACH-eligible part detaches (D3; structural parts get a
	/// sentinel that is never reached).</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public float DetachImpulse => detach_impulse ?? DamageDefaults.For( kind, part, DimsM ).detach;
	/// <summary>Depth (metres) added per (N·s) of impulse ABOVE <see cref="DentImpulse"/>.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public float Stiffness => stiffness ?? DamageDefaults.For( kind, part, DimsM ).stiffness;
	/// <summary>Per-part crumple ceiling in metres (kernel saturation clamp + damage-fraction denom).</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public float MaxCrushM => max_crush_m ?? DamageDefaults.For( kind, part, DimsM ).crush;
	/// <summary>Zone tag driving functional coupling.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public string Zone => zone ?? DamageDefaults.Zone( kind, part );

	/// <summary>True when this part accepts plastic dents (wheels never do — they are the physics
	/// contract).</summary>
	public bool IsDentable => kind != "wheel";

	/// <summary>Fill any omitted schema-v3 damage field with its kind/dims default IN PLACE (called
	/// once by <see cref="PartKitManifest.TryLoad"/> so downstream reads are compensation-free).</summary>
	public void ApplyDamageDefaults()
	{
		var d = DamageDefaults.For( kind, part, DimsM );
		dent_impulse ??= d.dent;
		loosen_impulse ??= d.loosen;
		detach_impulse ??= d.detach;
		stiffness ??= d.stiffness;
		max_crush_m ??= d.crush;
		zone ??= DamageDefaults.Zone( kind, part );
	}
}

/// <summary>
/// Per-kind default destruction band (schema v3). These are the SAME numbers the generator writes
/// (tools/gen_vehicle.py <c>damage_defaults()</c>) and the loader back-fills for older manifests —
/// keep them LOCKSTEP so a v1/v2 kit normalized at load matches a freshly generated v3 kit.
/// Impulses are N·s (mass·|Δv| across a contact tick); stiffness is metres of dent per N·s over the
/// dent threshold; crush is the metres ceiling. Values are D1 starting points — live tuning is a
/// later D1 pass.
/// </summary>
public static class DamageDefaults
{
	public struct Band { public float dent, loosen, detach, stiffness, crush; }

	// kind → (dentImpulse, loosenMul, detachMul[null = non-detachable → sentinel], stiffness, crushFrac)
	// crush = clamp(crushFrac · min(dims), 0.05, 0.30) m; wheels are a never-dent sentinel profile.
	const float NoDetach = 1_000_000_000f; // structural parts: effectively never detach in v1

	public static Band For( string kind, string part, Vector3 dims )
	{
		float minDim = MathF.Min( dims.x, MathF.Min( dims.y, dims.z ) );
		float dent, loosenMul, detachMul, stiff, crushFrac;
		bool detachable = true;
		switch ( kind )
		{
			case "chassis": dent = 2600f; loosenMul = 2.5f; detachMul = 0f; detachable = false; stiff = 3.0e-5f; crushFrac = 0.18f; break;
			case "bed": dent = 2200f; loosenMul = 2.2f; detachMul = 6.0f; stiff = 3.5e-5f; crushFrac = 0.16f; break;
			case "door": dent = 1500f; loosenMul = 2.0f; detachMul = 4.5f; stiff = 5.0e-5f; crushFrac = 0.30f; break;
			case "hood": dent = 1100f; loosenMul = 2.4f; detachMul = 4.0f; stiff = 7.0e-5f; crushFrac = 0.30f; break;
			case "trunk": dent = 1200f; loosenMul = 2.2f; detachMul = 4.0f; stiff = 6.5e-5f; crushFrac = 0.30f; break;
			case "tailgate": dent = 1200f; loosenMul = 2.0f; detachMul = 3.5f; stiff = 6.5e-5f; crushFrac = 0.30f; break;
			case "bolton": dent = 800f; loosenMul = 2.2f; detachMul = 3.2f; stiff = 8.0e-5f; crushFrac = 0.35f; break;
			case "fascia": dent = 750f; loosenMul = 2.0f; detachMul = 3.0f; stiff = 9.0e-5f; crushFrac = 0.30f; break;
			case "mirror": dent = 250f; loosenMul = 1.6f; detachMul = 2.2f; stiff = 1.2e-4f; crushFrac = 0.30f; break;
			case "accessory": dent = 400f; loosenMul = 1.8f; detachMul = 2.5f; stiff = 1.0e-4f; crushFrac = 0.30f; break;
			case "wheel": return new Band { dent = NoDetach, loosen = NoDetach, detach = NoDetach, stiffness = 0f, crush = 0f };
			default: dent = 1500f; loosenMul = 2.2f; detachMul = 4.0f; stiff = 5.0e-5f; crushFrac = 0.25f; break;
		}
		float crush = Math.Clamp( crushFrac * (minDim > 0f ? minDim : 0.5f), 0.05f, 0.30f );
		return new Band
		{
			dent = dent,
			loosen = dent * loosenMul,
			detach = detachable ? dent * detachMul : NoDetach,
			stiffness = stiff,
			crush = crush,
		};
	}

	/// <summary>Default zone tag: front-corner steering parts vs hood vs rear vs door vs
	/// cabin vs never-dent wheel. Bolt-ons/fascia split front/rear by part name.</summary>
	public static string Zone( string kind, string part )
	{
		string p = part ?? "";
		switch ( kind )
		{
			case "wheel": return "wheel";
			case "chassis": return "cabin";
			case "hood": return "hood";
			case "door": return "door";
			case "mirror": return "door";
			case "trunk": return "rear";
			case "tailgate": return "rear";
			case "bed": return "rear";
			case "accessory": return "rear";
			case "fascia": return "front";
			case "bolton": return p.Contains( "_r" ) || p.Contains( "rear" ) ? "rear" : "front";
			default: return "cabin";
		}
	}
}
