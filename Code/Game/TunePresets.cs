namespace VehicleProto;

/// <summary>
/// One saved player tune for ONE car — the exact dial set <c>TuningPanel.BuildParams</c> edits,
/// stored as ABSOLUTE values (not subtraction-deltas) so a load reconstructs the tuned state by
/// pushing each value straight back through the panel's own <c>p.Set(...)</c> paths. The three
/// coupled fields are persisted the same way the panel re-applies them:
/// <list type="bullet">
/// <item><b>Redline / shift points</b> — <see cref="RedlineRpm"/> plus its derived
/// <see cref="ShiftUpRpm"/>/<see cref="ShiftDownRpm"/> are ALL stored. A load restores all three
/// verbatim (matching whatever <c>ScaleShiftPoints</c> produced at save time), then the panel
/// re-derives its shift-point fractions from them so a subsequent Redline dial edit still scales
/// from the loaded baseline. Storing only redline and re-scaling would drift, because the fractions
/// depend on the car's pristine definition, not the tuned one.</item>
/// <item><b>Grip</b> — the panel's <c>_gripScale</c> MULTIPLIER (SetGrip scales the pristine tire
/// curves), never the raw curve points. A load re-applies it through the same <c>SetGrip</c> path.</item>
/// <item><b>Gravity</b> — a SCENE property, not a per-car field. Captured for parity so a loaded
/// preset drives identically to when it was saved, and re-applied via <c>SetGravity</c> on load.</item>
/// </list>
/// Presets are keyed by <see cref="CarId"/> (<c>CarSwitcher.CurrentId</c>: hatch/coupe/kart/pickup)
/// so a coupe tune never applies to the hatch.
/// </summary>
public class TunePreset
{
	public string CarId { get; set; }
	public string Name { get; set; }

	// engine
	public float PeakTorque { get; set; }
	public float FinalDrive { get; set; }
	public float LaunchBoost { get; set; }
	public float EngineBrakeTorque { get; set; }
	public float RedlineRpm { get; set; }
	public float ShiftUpRpm { get; set; }   // coupled to RedlineRpm via ScaleShiftPoints
	public float ShiftDownRpm { get; set; } // coupled to RedlineRpm via ScaleShiftPoints

	// tires / brakes
	public float GripScale { get; set; } = 1f;   // the panel's _gripScale multiplier, re-applied via SetGrip
	public float HandbrakeGripScale { get; set; }
	public float BrakeTorque { get; set; }
	public float BrakeAssist { get; set; }
	public float HandbrakeTorque { get; set; }

	// suspension
	public float SpringRate { get; set; }
	public float DamperRate { get; set; }
	public float Mass { get; set; }
	public float GravityScale { get; set; } = 1.1f; // scene gravity in g (captured for parity)

	// steering / assists
	public float SteerRateScale { get; set; }
	public float MaxSteerAngle { get; set; }
	public float HighSpeedSteerAngle { get; set; }
	public float ReverseSpeedCap { get; set; }
	public float SpinRecoveryAssist { get; set; }
}

/// <summary>
/// Player-writable persistence for <see cref="TunePreset"/>s, one JSON file for all cars
/// (<c>tune_presets.json</c> in <see cref="FileSystem.Data"/> — the per-user writable store that
/// works in a published build; the house save/load convention).
/// The file is loaded once, cached in memory, and rewritten on every add/delete. All access is by
/// car id so the TuningPanel only ever sees the active car's tunes.
/// </summary>
public static class TunePresetStore
{
	public const string FileName = "tune_presets.json";

	/// <summary>Wrapper so the JSON root is an object (forward-compatible: a schema version or extra
	/// top-level fields can be added later without breaking older files).</summary>
	class Doc
	{
		public int Version { get; set; } = 1;
		public List<TunePreset> Presets { get; set; } = new();
	}

	static Doc _doc;

	static Doc Data
	{
		get
		{
			if ( _doc is not null )
				return _doc;

			if ( FileSystem.Data.FileExists( FileName ) )
			{
				try
				{
					_doc = Json.Deserialize<Doc>( FileSystem.Data.ReadAllText( FileName ) );
				}
				catch ( Exception e )
				{
					Log.Warning( $"[vp] tune presets load failed ({e.Message}) — starting empty" );
				}
			}

			return _doc ??= new Doc();
		}
	}

	static void Flush()
	{
		try
		{
			FileSystem.Data.WriteAllText( FileName, Json.Serialize( Data ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[vp] tune presets save failed: {e.Message}" );
		}
	}

	/// <summary>The saved tunes for one car, in save order. Returns a fresh list (safe to hold in
	/// the UI); the elements are the live <see cref="TunePreset"/> instances so <see cref="Delete"/>
	/// matches by reference.</summary>
	public static IReadOnlyList<TunePreset> ForCar( string carId )
		=> Data.Presets
			.Where( p => string.Equals( p.CarId, carId, StringComparison.OrdinalIgnoreCase ) )
			.ToList();

	/// <summary>Persist a new preset (append + rewrite the file).</summary>
	public static void Add( TunePreset preset )
	{
		if ( preset is null )
			return;
		Data.Presets.Add( preset );
		Flush();
	}

	/// <summary>Remove a preset (by reference) and rewrite the file. No-op if it isn't stored.</summary>
	public static void Delete( TunePreset preset )
	{
		if ( preset is null )
			return;
		if ( Data.Presets.Remove( preset ) )
			Flush();
	}

	/// <summary>Rename a stored preset (player rename via the TuningPanel edit chip). Mutates the live
	/// instance and rewrites the file — the UI holds the same reference (see <see cref="ForCar"/>), so
	/// the new name shows immediately. Rules: empty/whitespace is REJECTED (keeps the old name); names
	/// stay UNIQUE per car (case-insensitive, the same key space <see cref="NextName"/> auto-names into)
	/// — a collision with a DIFFERENT same-car preset auto-suffixes " (2)", " (3)"…; a no-op rename to the
	/// current name changes nothing. Returns true only when the stored name actually changed.</summary>
	public static bool Rename( TunePreset preset, string newName )
	{
		if ( preset is null )
			return false;

		newName = newName?.Trim();
		if ( string.IsNullOrWhiteSpace( newName ) )
			return false; // reject empty — keep the old name

		if ( string.Equals( preset.Name, newName, StringComparison.Ordinal ) )
			return false; // no-op (identical) — don't churn the file

		// Unique per car: exclude the preset itself, then auto-suffix past any collision with a sibling.
		var taken = new HashSet<string>(
			ForCar( preset.CarId )
				.Where( p => !ReferenceEquals( p, preset ) )
				.Select( p => p.Name ?? "" ),
			StringComparer.OrdinalIgnoreCase );

		var unique = newName;
		for ( int n = 2; taken.Contains( unique ); n++ )
			unique = $"{newName} ({n})";

		preset.Name = unique;
		Flush();
		return true;
	}

	/// <summary>Auto-name for the next tune of this car: "<c>&lt;carId&gt; tune N</c>", where N is the
	/// lowest positive integer not already used by a same-car preset (so deleting "coupe tune 1" frees
	/// that name again). Named this way because a live in-panel text field is impractical here — the
	/// TuningPanel rebuilds every frame off live suspension load, which would destroy a focused
	/// TextEntry mid-type.</summary>
	public static string NextName( string carId )
	{
		var used = new HashSet<string>(
			ForCar( carId ).Select( p => p.Name ?? "" ),
			StringComparer.OrdinalIgnoreCase );

		for ( int n = 1; ; n++ )
		{
			var candidate = $"{carId} tune {n}";
			if ( !used.Contains( candidate ) )
				return candidate;
		}
	}
}
