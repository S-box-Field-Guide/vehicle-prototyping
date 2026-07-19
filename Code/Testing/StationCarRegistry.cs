namespace VehicleProto;

/// <summary>
/// Canonical station/car resolution, shared by the play-mode pilot, the editor McpTools
/// (<c>Editor/VpTools.cs</c>), <see cref="CarSwitcher"/>, and the part-kit console commands.
/// Reconciles the station ids used by <c>specs/maneuvers/*.json</c> + <c>vp_test.py</c> with the
/// authoritative <see cref="TestTrack.Stations"/> registry, maps car ids to
/// <see cref="CarDefinitions"/>, and picks a default station per maneuver for the ConVar on-ramp.
///
/// Extracted verbatim from <see cref="VehiclePilot"/> in the pilot decomposition.
/// <see cref="VehiclePilot.ResolveCar"/> /
/// <see cref="VehiclePilot.ResolveStation"/> remain as thin forwarders so external callers that
/// are out of that task's edit scope (VpTools, PartKitCommands) keep compiling unchanged — this is
/// the ONE home for the logic, the pilot just re-exports the seam.
/// </summary>
public static class StationCarRegistry
{
	/// <summary>Alias table: spec/station ids used by specs/maneuvers/*.json and vp_test.py's
	/// STATIONS set, mapped onto the authoritative TestTrack.Stations keys. Chosen over renaming
	/// the registry so both the drafted specs AND the descriptive registry keys keep working.</summary>
	static readonly Dictionary<string, string> StationAliases = new( StringComparer.OrdinalIgnoreCase )
	{
		["openpad"] = "jturnpad",
		["jturn"] = "jturnpad",
		["hillgrade"] = "hillclimb",
		// crashwall -> the RESERVED plot only (crash/destruction is out of scope for this kit). The
		// station is kept as a reference/reserved spawn point; no `crash` maneuver consumes it here.
		["crashwall"] = "crashwall_reserved",
	};

	/// <summary>Every station id <see cref="ResolveStation"/> accepts — the authoritative
	/// <see cref="TestTrack.Stations"/> keys plus the spec aliases. Empty until the world is built.
	/// vp_status returns this as the measurement-world census so the runner can fail closed on a
	/// referenced-but-absent station BEFORE it drives (audit 2026-07-13 HIGH).</summary>
	public static IEnumerable<string> ResolvableStationIds()
	{
		var stations = TestTrack.Stations;
		if ( stations is null )
			yield break;
		foreach ( var k in stations.Keys )
			yield return k;
		// aliases only resolve when their target station exists
		foreach ( var kv in StationAliases )
			if ( stations.ContainsKey( kv.Value ) )
				yield return kv.Key;
	}

	public static bool ResolveStation( string id, out Vector3 posMeters, out Rotation facing )
	{
		posMeters = Vector3.Zero;
		facing = Rotation.Identity;
		if ( string.IsNullOrEmpty( id ) )
			return false;

		var key = id.Trim();

		// COORDINATE SPAWN (world pass 2026-07-19): "at:x,y[,yawDeg]" resolves to raw world METRES —
		// the automation escape hatch for worlds that have no station registry (the playground stunt
		// park). Resolved BEFORE the TestTrack lookup so it works when Stations is empty. Deliberately
		// NOT listed in ResolvableStationIds(): the battery's measurement-world census only ever sees
		// real proto stations, so no spec can quietly anchor a measurement to a magic coordinate.
		if ( key.StartsWith( "at:", System.StringComparison.OrdinalIgnoreCase ) )
		{
			var parts = key[3..].Split( ',' );
			if ( parts.Length is < 2 or > 3 )
				return false;
			if ( !TryParseInvariant( parts[0], out float x ) || !TryParseInvariant( parts[1], out float y ) )
				return false;
			float yaw = 0f;
			if ( parts.Length == 3 && !TryParseInvariant( parts[2], out yaw ) )
				return false;
			posMeters = new Vector3( x, y, 0f );
			facing = Rotation.FromYaw( yaw );
			return true;
		}

		var stations = TestTrack.Stations;
		if ( stations is null || stations.Count == 0 )
			return false;

		if ( !stations.TryGetValue( key, out var s ) )
		{
			if ( !StationAliases.TryGetValue( key, out var aliased ) || !stations.TryGetValue( aliased, out s ) )
				return false;
		}

		posMeters = s.posMeters;
		facing = s.facing;
		return true;
	}

	/// <summary>Locale-proof float parse for the "at:" coordinate syntax (a comma-decimal system
	/// locale must not change how automation coordinates read).</summary>
	static bool TryParseInvariant( string s, out float value )
		=> float.TryParse( s, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out value );

	public static CarDefinition ResolveCar( string id )
		=> (id ?? "").Trim().ToLowerInvariant() switch
		{
			"kart" => CarDefinitions.Kart,
			"coupe" => CarDefinitions.Coupe,
			"pickup" => CarDefinitions.Pickup,     // pickup part kit
			_ => CarDefinitions.Hatch,             // default + "hatch" = the hatch_kit part kit
		};

	/// <summary>Strict car resolution for AUTOMATION callers (vp_spawn/vp_drive): an UNKNOWN id
	/// returns false rather than silently selecting the hatch (audit 2026-07-13 MEDIUM — an
	/// acknowledgement must mean the requested car was valid). The player-facing UI paths
	/// (<see cref="CarSwitcher"/>, session menu) keep the forgiving <see cref="ResolveCar"/>
	/// fallback-to-hatch on purpose.</summary>
	public static readonly string[] KnownCarIds = { "hatch", "coupe", "kart", "pickup" };

	public static bool TryResolveCar( string id, out CarDefinition def )
	{
		var key = (id ?? "").Trim().ToLowerInvariant();
		if ( Array.IndexOf( KnownCarIds, key ) < 0 )
		{
			def = null;
			return false;
		}
		def = ResolveCar( key );
		return true;
	}

	public static string DefaultStationFor( string man ) => man switch
	{
		"launch" or "topspeed" => "dragstrip",
		"brake" => "brakezone",
		"skidpad" or "figure8" => "skidpad",
		"slalom" => "slalom",
		"jturn" or "spinrecovery" => "jturnpad",
		"jump" => "ramps",
		"washboard" => "washboard",
		"hillclimb" => "hillclimb",
		"liftoff" => "bankedcurve",
		// "crash" is out of scope for this kit (no destruction sim) — no default station here; a
		// crash maneuver is unknown in this repo and the runner rejects it before drive.
		_ => "dragstrip",
	};
}
