namespace VehicleProto;

/// <summary>
/// Name → maneuver-object table (the pilot decomposition). One cached
/// singleton per maneuver — they carry only per-run phase state, which <see cref="IManeuver.Start"/>
/// re-arms each run, so a shared instance is safe and cheap. This is the ONE place a new maneuver is
/// registered: add its class + one line here + a spec file (docs/testing-harness.md §8). Unknown
/// names return null, which the pilot surfaces as the same "unknown maneuver" error as before.
/// </summary>
public static class ManeuverRegistry
{
	static readonly Dictionary<string, IManeuver> _registry = new( StringComparer.OrdinalIgnoreCase )
	{
		["launch"] = new LaunchManeuver(),
		["topspeed"] = new TopSpeedManeuver(),
		["brake"] = new BrakeManeuver(),
		["skidpad"] = new SkidpadManeuver(),
		["slalom"] = new SlalomManeuver(),
		["jturn"] = new JTurnManeuver(),
		["jump"] = new JumpManeuver(),
		// ["crash"] is intentionally absent — full crash/destruction simulation is out of
		// scope for this prototyping kit.
		// wave-2 maneuver pilot profiles (docs/testing-harness.md §8). ALL FOUR added in
		// ONE edit — an in-editor hotload migrates this static dict VALUE and hides entries added
		// piecemeal, so add them together rather than piecemeal.
		["liftoff"] = new LiftoffManeuver(),
		["washboard"] = new WashboardManeuver(),
		["hillclimb"] = new HillClimbManeuver(),
		["figure8"] = new Figure8Maneuver(),
		// feel session 2026-07-13: drift-exit recovery probe.
		// Adding a registry entry migrates this static-readonly dict VALUE on an in-editor hotload —
		// if a live run 404s "unknown maneuver 'driftexit'" on a green compile, rename-bust the field
		// rather than chasing a phantom bug.
		["driftexit"] = new DriftExitManeuver(),
		// feel session 2026-07-15: spin-recovery probe (backward-slide kill after a handbrake spin).
		// Same hotload caveat as the entries above — a live "unknown maneuver 'spinrecovery'" on a green
		// compile means the static dict VALUE didn't migrate; bump a source mtime to rebuild.
		["spinrecovery"] = new SpinRecoveryManeuver(),
		["route"] = new RouteManeuver(),
	};

	/// <summary>The registered maneuver for <paramref name="name"/>, or null if unmapped.</summary>
	public static IManeuver Get( string name )
		=> !string.IsNullOrEmpty( name ) && _registry.TryGetValue( name.Trim(), out var m ) ? m : null;
}
