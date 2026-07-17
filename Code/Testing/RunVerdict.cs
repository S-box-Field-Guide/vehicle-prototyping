namespace VehicleProto;

/// <summary>
/// One metric row in a <see cref="RunVerdict"/>: carries BOTH projections of a single measured
/// value so the compact console <c>[vp] RUN … done</c> line and the in-game verdict-card row derive
/// from one definition instead of the two hand-kept switch statements the monolithic pilot had.
/// </summary>
public sealed class VerdictMetric
{
	public string LogToken = ""; // "zeroToHundredS=9.92"  (console line fragment)
	public string Label = "";    // "0-100 km/h"           (card row label)
	public string Value = "";    // "9.92 s"               (card row value)
}

/// <summary>
/// The shared per-run verdict DTO (the pilot decomposition). Built at
/// FinishRun by asking the active <see cref="IManeuver"/> to <see cref="IManeuver.Report"/> its
/// metric rows, then snapshotting the standing invariant audits. It is the SINGLE source the
/// console run-line (<see cref="Line"/>) and the in-game verdict card (<see cref="ApplyToUiFeed"/>)
/// both consume — killing the previously tri-maintained metric/verdict contract across EmitRunLine,
/// BuildMetricRows, and the inline UiFeed pass/fail check. Metric NAMES stay the frozen contract
/// (docs/testing-harness.md §6.2); this only unifies the C#-side projection.
/// </summary>
public sealed class RunVerdict
{
	public string Maneuver = "";
	public string Car = "";
	public string Message = "";
	public readonly List<VerdictMetric> Metrics = new();
	/// <summary>Standing invariant audits (name, offenders), target 0 — the "invariants only" asserts.</summary>
	public readonly List<(string Name, int Offenders)> Invariants = new();
	public string YawSummary = "";  // jturn: "catchable"/"spun"; blank hides the card row
	public string Line = "";        // the full "[vp] RUN … done …" console line

	/// <summary>Add a metric row (both projections from one call).</summary>
	public void Add( string logToken, string label, string value )
		=> Metrics.Add( new VerdictMetric { LogToken = logToken, Label = label, Value = value } );

	/// <summary>All standing invariants clean AND no error/timeout message — the in-game card's
	/// pass test, identical to the old inline UiFeed.LastRunPass expression.</summary>
	public bool InvariantsPass
		=> Invariants.All( a => a.Offenders == 0 ) && string.IsNullOrEmpty( Message );

	/// <summary>Snapshot the completed run into a verdict: maneuver metric rows + invariant audits +
	/// the byte-identical console run-line. <paramref name="active"/> may be null (unmapped maneuver)
	/// — then the default elapsedS projection is used, matching the old EmitRunLine default.</summary>
	public static RunVerdict Build( string maneuver, string message, IManeuver active )
	{
		var v = new RunVerdict { Maneuver = maneuver, Car = VehicleBridge.Car, Message = message };

		active?.Report( v );
		if ( v.Metrics.Count == 0 )
			v.Add( $"elapsedS={VehicleBridge.ElapsedS:F1}", "Elapsed", $"{VehicleBridge.ElapsedS:F1} s" );

		v.Invariants.Add( ("Flips", VehicleBridge.Flips) );
		v.Invariants.Add( ("Fall-throughs", VehicleBridge.FallThroughs) );
		v.Invariants.Add( ("Stuck ticks", VehicleBridge.StuckTicks) );
		v.Invariants.Add( ("NaN forces", VehicleBridge.NanForces) );
		v.Invariants.Add( ("Sleep while driving", VehicleBridge.SleepWhileDriving) );

		string metric = string.Join( " ", v.Metrics.Select( m => m.LogToken ) );
		v.Line = $"[vp] RUN {maneuver} car={VehicleBridge.Car} done {metric} " +
			$"distanceM={VehicleBridge.DistanceM:F1} maxSpeedMs={VehicleBridge.MaxSpeedMs:F1}";
		return v;
	}

	/// <summary>Human events summary for the card's "Assist events" row (unchanged from the old
	/// BuildEventsSummary — TC/ABS/spun/flips).</summary>
	public string EventsSummary()
	{
		var parts = new List<string>();
		if ( VehicleBridge.WheelspinS > 0.1f ) parts.Add( $"TC {VehicleBridge.WheelspinS:F1}s" );
		if ( VehicleBridge.LockupTicks > 0 ) parts.Add( $"ABS x{VehicleBridge.LockupTicks}" );
		if ( VehicleBridge.SpunOut ) parts.Add( "spun out" );
		if ( VehicleBridge.Flips > 0 ) parts.Add( $"flips x{VehicleBridge.Flips}" );
		return string.Join( ", ", parts );
	}

	/// <summary>Project this verdict onto the harness-fed UI buffer. The card is
	/// still "invariants only" (handling-target bands stay CLI-side — testing-harness.md §7.4), but
	/// now shows the invariant asserts as REAL per-assert rows instead of one aggregate bool. No
	/// panel/scss change — the same UiMetric render path carries the extra rows.</summary>
	public void ApplyToUiFeed( string car )
	{
		UiFeed.HasLastRun = true;
		UiFeed.LastRunTitle = $"{Maneuver} · {car} · invariants only";
		UiFeed.LastRunPass = InvariantsPass;
		UiFeed.LastRunOutOfBand = false; // band verdicts stay CLI-side until specs reach the game assembly
		UiFeed.LastRunYaw = YawSummary;
		UiFeed.LastRunEvents = EventsSummary();
		UiFeed.LastRunLine = Line;

		UiFeed.LastRunMetrics.Clear();
		foreach ( var m in Metrics )
			UiFeed.LastRunMetrics.Add( new UiMetric { Label = m.Label, Value = m.Value, Band = "", Pass = true, HasBar = false } );
		// per-assert invariant rows (target 0): red value = an offender, so the card can no longer
		// present a passing metric set while an invariant is nonzero (audit 2026-07-12 MEDIUM).
		foreach ( var (name, offenders) in Invariants )
			UiFeed.LastRunMetrics.Add( new UiMetric { Label = name, Value = offenders.ToString(), Band = "", Pass = offenders == 0, HasBar = false } );
	}
}
