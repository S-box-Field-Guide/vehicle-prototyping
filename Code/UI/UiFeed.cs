namespace VehicleProto;

/// <summary>
/// Harness-fed display buffer for widgets that are NOT bound to a live vehicle member:
/// timing splits, the last-run verdict card, and best times. This UI layer OWNS this
/// class; the bootstrap wires VehiclePilot -> UiFeed after the harness lands
/// (zero references to bridge/pilot types from here). Every field is a plain display
/// value already formatted for the panel — panels render straight from here.
///
/// All fields start in a graceful "idle / no run yet" state so the panels render the
/// design's armed/placeholder look before any harness run exists.
/// </summary>
public static class UiFeed
{
	// ---- timing widget (TelemetryPanel, top-center) ----
	public static string TimingTitle   = "0–100 km/h"; // metric being timed
	public static string TimingContext = "";           // "launch · run 7"
	public static bool   TimingRunning = false;         // cyan border + live value when true
	public static string TimingValue   = "—";           // "3.42"
	public static string TimingUnit    = "s";
	public static string TimingBest    = "";            // "best 5.72" (blank = none)
	public static string TimingExtra1  = "¼ mile —";    // three sub-readouts along the footer
	public static string TimingExtra2  = "";
	public static string TimingExtra3  = "";

	// ---- last-run verdict card (TelemetryPanel, right) ----
	public static bool   HasLastRun     = false;        // hide the card until a run completes
	public static string LastRunTitle   = "";           // "skidpad · coupe"
	public static bool   LastRunPass    = true;
	public static bool   LastRunOutOfBand = false;      // shows the amber OUT OF BAND chip
	public static string LastRunYaw     = "";           // "clean"  (blank = row hidden)
	public static string LastRunEvents  = "";           // "TC ×2"  (blank = row hidden)
	public static string LastRunLine    = "";           // raw "[vp] RUN skidpad car=coupe PASS latg=0.98"
	public static readonly List<UiMetric> LastRunMetrics = new();

	/// <summary>Session-reset — call from the boot singleton so stale runs don't survive Play→Stop→Play.</summary>
	public static void Reset()
	{
		TimingTitle = "0–100 km/h";
		TimingContext = "";
		TimingRunning = false;
		TimingValue = "—";
		TimingUnit = "s";
		TimingBest = "";
		TimingExtra1 = "¼ mile —";
		TimingExtra2 = "";
		TimingExtra3 = "";
		HasLastRun = false;
		LastRunTitle = "";
		LastRunPass = true;
		LastRunOutOfBand = false;
		LastRunYaw = "";
		LastRunEvents = "";
		LastRunLine = "";
		LastRunMetrics.Clear();
	}

	/// <summary>Cheap content hash so panels' BuildHash can detect a harness push.</summary>
	public static int RunHash()
	{
		var h = new HashCode();
		h.Add( TimingValue ); h.Add( TimingRunning ); h.Add( TimingBest ); h.Add( TimingContext );
		h.Add( HasLastRun ); h.Add( LastRunLine ); h.Add( LastRunPass ); h.Add( LastRunOutOfBand );
		foreach ( var m in LastRunMetrics ) h.Add( m.Value );
		return h.ToHashCode();
	}
}

/// <summary>One metric row in the run-verdict card: value against an optional band bar.</summary>
public sealed class UiMetric
{
	public string Label = "";   // "Lateral g"
	public string Value = "";   // "0.98 g"
	public string Band  = "";   // "band 0.95 – 1.05" (blank = no band bar)
	public bool   Pass  = true; // colors the value + marker
	public bool   HasBar;       // draw the band bar
	public float  Frac;         // 0..1 marker position along the bar
	public float  BandLo;       // 0..1 band start
	public float  BandHi;       // 0..1 band end
}
