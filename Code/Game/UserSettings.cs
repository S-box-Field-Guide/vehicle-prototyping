namespace VehicleProto;

/// <summary>Player-facing speed readout unit. DISPLAY ONLY — every measurement, test band, and
/// telemetry capture stays SI (m/s); this enum only picks the last-mile formatting in the HUD.</summary>
public enum SpeedUnit
{
	Kmh = 0,   // default — km/h (current behaviour; byte-identical for existing players)
	Mph = 1,
}

/// <summary>
/// The serialized on-disk shape of the player's local preferences. One object so future settings
/// (camera prefs, colour-blind palette, etc.) can join it without a new file or a schema break —
/// same forward-compatible <c>Version</c> + object-root convention as <see cref="TunePresetStore"/>.
/// </summary>
public class UserSettingsData
{
	public int Version { get; set; } = 1;
	public SpeedUnit SpeedUnit { get; set; } = SpeedUnit.Kmh;
}

/// <summary>
/// Player-writable local preferences, one JSON file in <see cref="FileSystem.Data"/>
/// (<c>user_settings.json</c> — the per-user store that works in a published build; the house
/// save/load convention, mirrored from <see cref="TunePresetStore"/>). Loaded lazily once, cached
/// in memory, rewritten on every change. A missing file → all defaults, so an existing player who
/// never touched the setting keeps the current km/h behaviour byte-for-byte.
/// </summary>
public static class UserSettings
{
	public const string FileName = "user_settings.json";

	// m/s → display-unit multipliers (exact: 1 m/s = 3.6 km/h = 2.2369362920544 mph).
	const float MsToKmh = 3.6f;
	const float MsToMph = 2.23694f;

	static UserSettingsData _data;

	static UserSettingsData Data
	{
		get
		{
			if ( _data is not null )
				return _data;

			if ( FileSystem.Data.FileExists( FileName ) )
			{
				try
				{
					_data = Json.Deserialize<UserSettingsData>( FileSystem.Data.ReadAllText( FileName ) );
				}
				catch ( Exception e )
				{
					Log.Warning( $"[vp] user settings load failed ({e.Message}) — using defaults" );
				}
			}

			return _data ??= new UserSettingsData();
		}
	}

	/// <summary>Force the file to load now (default km/h if absent). Optional warm-up — access is
	/// lazy, so calling this at boot is purely to surface a corrupt-file warning early.</summary>
	public static void Load() => _ = Data;

	static void Flush()
	{
		try
		{
			FileSystem.Data.WriteAllText( FileName, Json.Serialize( Data ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[vp] user settings save failed: {e.Message}" );
		}
	}

	/// <summary>The selected speed unit. Set persists immediately (same idiom as a preset add) and
	/// is a no-op if unchanged, so re-picking the active unit doesn't rewrite the file.</summary>
	public static SpeedUnit SpeedUnit
	{
		get => Data.SpeedUnit;
		set
		{
			if ( Data.SpeedUnit == value )
				return;
			Data.SpeedUnit = value;
			Flush();
		}
	}

	/// <summary>Convert an SI speed (m/s) to the player's chosen display unit. Formatting only —
	/// callers pass the same m/s value they'd have shown as km/h; the internal number is untouched.</summary>
	public static float ToDisplaySpeed( float metersPerSecond )
		=> metersPerSecond * (Data.SpeedUnit == SpeedUnit.Mph ? MsToMph : MsToKmh);

	/// <summary>The unit label to render next to a converted speed ("km/h" / "mph").</summary>
	public static string SpeedUnitLabel => Data.SpeedUnit == SpeedUnit.Mph ? "mph" : "km/h";
}
