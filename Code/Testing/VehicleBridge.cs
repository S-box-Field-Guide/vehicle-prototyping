namespace VehicleProto;

/// <summary>
/// Static command/report facade between the EDITOR McpTools (<c>vp_spawn/vp_drive/vp_status/
/// vp_audit</c> in <c>Editor/VpTools.cs</c>, editor assembly) and the PLAY-MODE director
/// (<see cref="VehiclePilot"/>, game assembly). An [McpTool] is one synchronous request; a
/// scripted maneuver runs over many game fixed-ticks, so the tool POSTS a command into these
/// game-assembly statics and the pilot CONSUMES it on its next FixedUpdate, writing the live
/// telemetry report back here for the tool's next <c>{op:"status"}</c> poll.
///
/// Signalling is a plain monotonic <see cref="int"/> token compare — NO <c>System.Threading</c>
/// (<c>Volatile</c>/<c>Interlocked</c>) in the game assembly (the whitelist forbids them;
/// an int compare is torn-read-proof on x64). <see cref="ResetSession"/> is called from the boot
/// singleton BEFORE any command is read so stale state can't make a fresh Play session no-op
/// (the Play→Stop→Play stale-static trap).
///
/// Report property names are PascalCase here; <c>VpTools</c> serializes them to the FROZEN
/// camelCase telemetry contract (docs/testing-harness.md §6.2). Keep the two in lockstep.
/// </summary>
public static class VehicleBridge
{
	public const string HarnessVersion = "m0-c-2026-07-11";

	// ── COMMAND (editor → game) ──────────────────────────────────────────────
	static int _cmdToken;
	static int _consumedToken;

	public enum Op { None, Spawn, Maneuver, Route, Stop, Reset }

	public static Op PendingOp { get; private set; }
	public static string CmdCar { get; private set; } = "hatch";
	public static string CmdStation { get; private set; } = "";
	public static string CmdManeuver { get; private set; } = "";
	/// <summary>Maneuver params, already parsed to floats editor-side (keeps JSON out of the game
	/// assembly). Bools/ints come through as 0/1 / rounded floats.</summary>
	public static IReadOnlyDictionary<string, float> CmdParams { get; private set; }
	/// <summary>Route waypoints as a flat [x0,y0,x1,y1,…] world-METER array (op:route).</summary>
	public static float[] CmdRoute { get; private set; }

	/// <summary>Post a command; returns the new token. Editor thread calls this; the pilot picks
	/// it up on its next FixedUpdate via <see cref="HasPending"/>.</summary>
	public static int Post( Op op, string car = null, string station = null, string maneuver = null,
		IReadOnlyDictionary<string, float> pars = null, float[] route = null )
	{
		PendingOp = op;
		if ( !string.IsNullOrEmpty( car ) ) CmdCar = car;
		CmdStation = station ?? "";
		CmdManeuver = maneuver ?? "";
		CmdParams = pars;
		CmdRoute = route;
		return ++_cmdToken;
	}

	public static bool HasPending => _cmdToken != _consumedToken;

	public static void MarkConsumed()
	{
		_consumedToken = _cmdToken;
		PendingOp = Op.None;
	}

	// ── PERF channel (editor vp_perf tool → PerfProbe) ───────────────────────
	// A SEPARATE token from the maneuver command channel so a perf capture never collides with a
	// scripted maneuver. Measurement-only: nothing here touches the physics/telemetry path.
	/// <summary>Monotonic perf-request token; <see cref="PerfProbe"/> runs a capture when it changes.</summary>
	public static int PerfToken { get; private set; }
	/// <summary>Requested capture mode: "idle" (car untouched), "drive" (deterministic measurement
	/// weave), or "census" (immediate one-shot scene census, no window).</summary>
	public static string PerfMode { get; private set; } = "idle";
	/// <summary>Requested capture window length (seconds); ignored for a census request.</summary>
	public static float PerfSeconds { get; private set; } = 30f;
	/// <summary>Probe lifecycle for the tool's status poll: idle | running | done.</summary>
	public static string PerfStatus { get; set; } = "idle";
	/// <summary>Last completed capture's headline numbers (for the tool's status readback; the
	/// authoritative record is always the greppable <c>[vp] PERF</c> console lines).</summary>
	public static float PerfLastSummaryFps { get; set; }
	public static float PerfLastP1LowFps { get; set; }
	public static float PerfLastPhysAvgMs { get; set; }

	/// <summary>Post a perf request; the always-present inert <see cref="PerfProbe"/> picks it up on
	/// its next Update. Returns the new token.</summary>
	public static int PostPerf( string mode, float seconds )
	{
		PerfMode = string.IsNullOrEmpty( mode ) ? "idle" : mode;
		PerfSeconds = seconds;
		PerfStatus = "running";
		return ++PerfToken;
	}

	// ── REPORT: envelope (game → editor) ─────────────────────────────────────
	public static bool DirectorAlive { get; set; }
	/// <summary>The world actually BUILT this play session ("proto" = measurement scene, "playground"
	/// = free-drive), set by <see cref="GameBootstrap"/> at boot. Empty until a world is built. The
	/// fail-closed measurement-world gate (audit 2026-07-13 HIGH) reads this via vp_status so the
	/// runner refuses to measure against playground/free-drive geometry.</summary>
	public static string World { get; set; } = "";
	/// <summary>Currently spawned/controlled car id (survives across runs).</summary>
	public static string SpawnedCar { get; set; } = "";
	/// <summary>idle | running | done | error — the runner polls until "done".</summary>
	public static string Status { get; set; } = "idle";
	public static int RunId { get; set; }
	public static string Maneuver { get; set; } = "";
	public static string Car { get; set; } = "";
	public static string Station { get; set; } = "";
	public static string Message { get; set; } = "";

	// ── REPORT: metrics (assertable; docs/testing-harness.md §6.2) ───────────
	public static float SpeedMs { get; set; }
	public static float MaxSpeedMs { get; set; }
	public static float DistanceM { get; set; }
	public static float ElapsedS { get; set; }
	public static float LateralGPeak { get; set; }
	public static float LateralGAvg { get; set; }
	public static float LongGPeak { get; set; }
	public static float LongGAvg { get; set; }
	public static float YawRatePeakDeg { get; set; }
	public static float YawRateAvgDeg { get; set; }
	public static float PitchDeg { get; set; }
	public static float RollDeg { get; set; }
	public static float HeadingDriftDeg { get; set; }
	public static int Gear { get; set; }
	public static int GearAtVmax { get; set; }
	public static float Rpm { get; set; }
	public static float WheelspinS { get; set; }
	public static float ZeroToHundredS { get; set; }
	public static float BrakeDistanceM { get; set; }
	public static int LockupTicks { get; set; }
	public static float AirtimeS { get; set; }
	public static float LandingPitchDeg { get; set; }
	public static float LandingRollDeg { get; set; }
	public static float SettleS { get; set; }
	public static float JturnTimeS { get; set; }
	public static float YawOvershootDeg { get; set; }
	public static int ConeStrikes { get; set; }
	public static float ContactLossPct { get; set; }
	/// <summary>Average % of wheel contact lost across the run (per-wheel IsGrounded; wave-2 washboard).</summary>
	public static float WheelContactLossPct { get; set; }
	public static float RollbackM { get; set; }
	// ── driftexit (feel session, 2026-07-13) ──
	/// <summary>Seconds from handbrake release to |rear slip angle| &lt; 8° (drift-exit recovery).</summary>
	public static float ExitRecoveryS { get; set; }
	/// <summary>exitSpeed / entrySpeed across the slide (momentum retention; higher = less scrub).</summary>
	public static float SpeedRetention { get; set; }
	/// <summary>Peak |rear slip angle| (deg) reached during the slide (how deep the drift got).</summary>
	public static float PeakSlipDeg { get; set; }
	// ── spinrecovery (feel session, 2026-07-15) ──
	/// <summary>Seconds from throttle-commit-after-spin (handbrake release) to forwardSpeed &gt; +0.5 m/s
	/// (the car actually driving its new-facing direction). Lower = stale backward velocity dies sooner.</summary>
	public static float RecoveryS { get; set; }

	// ── REPORT: standing invariant audits (target 0) ─────────────────────────
	public static int Flips { get; set; }
	public static int FlippedTicks { get; set; }
	public static int FallThroughs { get; set; }
	public static int StuckTicks { get; set; }
	public static int NanForces { get; set; }
	public static int SleepWhileDriving { get; set; }

	// ── REPORT: booleans ─────────────────────────────────────────────────────
	public static bool Catchable { get; set; }
	public static bool SpunOut { get; set; }
	public static bool Climbed { get; set; }

	/// <summary>Wipe per-session state and drop any un-consumed command. Called from the boot
	/// singleton's OnEnabled/OnStart BEFORE the pilot reads anything, so a leftover command or
	/// stale report from the previous Play session can't corrupt a fresh one.</summary>
	public static void ResetSession()
	{
		DirectorAlive = false;
		World = "";
		SpawnedCar = "";
		Status = "idle";
		RunId = 0;
		Message = "";
		ClearRunMetrics();
		MarkConsumed();
	}

	/// <summary>Reset the per-run report at the start of a fresh maneuver.</summary>
	public static void ResetRun( string maneuver, string car, string station )
	{
		RunId++;
		Status = "running";
		Maneuver = maneuver ?? "";
		Car = car ?? "";
		Station = station ?? "";
		Message = "";
		ClearRunMetrics();
	}

	static void ClearRunMetrics()
	{
		SpeedMs = MaxSpeedMs = DistanceM = ElapsedS = 0f;
		LateralGPeak = LateralGAvg = LongGPeak = LongGAvg = 0f;
		YawRatePeakDeg = YawRateAvgDeg = PitchDeg = RollDeg = HeadingDriftDeg = 0f;
		Gear = GearAtVmax = 0;
		Rpm = WheelspinS = ZeroToHundredS = BrakeDistanceM = 0f;
		LockupTicks = 0;
		AirtimeS = LandingPitchDeg = LandingRollDeg = SettleS = 0f;
		JturnTimeS = YawOvershootDeg = 0f;
		ConeStrikes = 0;
		ContactLossPct = WheelContactLossPct = RollbackM = 0f;
		ExitRecoveryS = SpeedRetention = PeakSlipDeg = 0f;
		RecoveryS = 0f;
		Flips = FlippedTicks = FallThroughs = StuckTicks = NanForces = SleepWhileDriving = 0;
		Catchable = false;
		SpunOut = false;
		Climbed = false;
	}
}
