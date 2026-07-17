using System;
using System.Text.Json.Nodes;
using Editor.Mcp;
using VehicleProto;

/// <summary>
/// Project-defined MCP tools for the vehicle_prototyping playtest harness
/// (docs/testing-harness.md §6 — the FROZEN contract). Thin wrappers over
/// <see cref="VehicleBridge"/>: the editor tool POSTs a command into the game-assembly statics
/// and the play-mode <see cref="VehiclePilot"/> consumes it next FixedUpdate, writing telemetry
/// back for the next <c>vp_drive {"op":"status"}</c> poll.
///
/// s&box calling convention: every tool takes a SINGLE JSON-string argument (the <c>call_tool</c>
/// <c>{"argsJson":"…"}</c> convention) and returns a single-encoded JSON string. Telemetry is
/// serialized to the EXACT camelCase field names of the frozen contract (docs/testing-harness.md
/// §6.2) — hand-built JsonObject so the keys can't drift under a serializer naming policy.
///
/// </summary>
public static class VpTools
{
	/// <summary>Read-only status: identity, scene, harness/bridge state (contract §6.1), plus the
	/// measurement-world gate fields (audit 2026-07-13 HIGH): <c>world</c> (the world built this
	/// session, or the pending <c>vp_world</c> ConVar before play), and a proving-ground station
	/// census (<c>worldBuilt</c>/<c>stationCount</c>/<c>stations</c>) so the runner can refuse to
	/// measure against playground/free-drive geometry or a missing station.</summary>
	[McpTool( "vp_status", Hints = McpToolHints.ReadOnly )]
	public static string Status()
	{
		var scene = SceneEditorSession.Active?.Scene ?? Game.ActiveScene;

		// World: prefer the world ACTUALLY built this play session; before play, fall back to the
		// vp_world ConVar (what the next play_start will build) so the preflight can catch a
		// persisted `vp_world playground` without needing to be in play mode.
		string world = VehicleBridge.DirectorAlive && !string.IsNullOrEmpty( VehicleBridge.World )
			? VehicleBridge.World
			: GameBootstrap.World;

		// Station census (only meaningful once the proto measurement world is built — TestTrack.Stations
		// is populated by TestTrack.Build). Names include the spec aliases ResolveStation accepts.
		var stationArr = new JsonArray();
		int stationCount = 0;
		bool worldBuilt = TestTrack.Stations is { Count: > 0 };
		if ( worldBuilt && string.Equals( world, "proto", StringComparison.OrdinalIgnoreCase ) )
		{
			foreach ( var id in StationCarRegistry.ResolvableStationIds() )
			{
				stationArr.Add( id );
				stationCount++;
			}
		}

		return new JsonObject
		{
			["project"] = "vehicle_prototyping",
			["scene"] = scene?.Name ?? "(none)",
			["compileOk"] = true, // authoritative compile gate is the built-in compile_status
			["isPlaying"] = VehicleBridge.DirectorAlive, // the pilot only lives in play mode
			["world"] = world,
			["worldBuilt"] = worldBuilt,
			["stationCount"] = stationCount,
			["stations"] = stationArr,
			["activeCar"] = VehicleBridge.SpawnedCar,
			["bridgeState"] = $"status={VehicleBridge.Status} pendingOp={VehicleBridge.PendingOp} runId={VehicleBridge.RunId} directorAlive={VehicleBridge.DirectorAlive}",
			["harnessVersion"] = VehicleBridge.HarnessVersion,
		}.ToJsonString();
	}

	/// <summary>Spawn car N at station S (omit both = reset in place). Seats at suspension
	/// equilibrium (contract §6.1; NOT surface+radius — the suspension-equilibrium gotcha). POSTs a Spawn command;
	/// the pilot does the actual spawn next tick. Returns the resolved car/station + computed
	/// seatZ (deterministic, so the editor-side value matches the pilot's).</summary>
	/// <param name="argsJson">e.g. '{"station":"dragstrip","car":"hatch"}'.</param>
	[McpTool( "vp_spawn" )]
	public static string Spawn( string argsJson = null )
	{
		var o = Parse( argsJson, out var err );
		if ( err != null ) return Error( err );

		string car = GetString( o, "car", "hatch" );
		string station = GetString( o, "station", "" );

		if ( !VehicleBridge.DirectorAlive )
			return Error( "no play-mode pilot — call play_start first (the pilot spawns/drives in play mode)" );

		// STRICT id validation for automation (audit 2026-07-13 MEDIUM): reject unknown car/station
		// with ok=false BEFORE posting, rather than silently falling back to hatch / reset-in-place.
		// The player-facing UI paths keep the forgiving fallback; automation must not.
		if ( !StationCarRegistry.TryResolveCar( car, out var def ) )
			return RejectCar( car );
		// station "" = reset in place (valid, omit-both contract). A NON-EMPTY unknown station is an error.
		bool stationOk = string.IsNullOrEmpty( station ) || VehiclePilot.ResolveStation( station, out _, out _ );
		if ( !stationOk )
			return RejectStation( station );

		VehicleBridge.Post( VehicleBridge.Op.Spawn, car: car, station: station );

		float seatZ = VehiclePilot.SeatHeightM( def );

		return new JsonObject
		{
			["ok"] = true,
			["car"] = car,
			["station"] = station,
			["seatZ"] = seatZ,
			["stationResolved"] = stationOk,
			["note"] = "spawn posted; consumed next FixedUpdate",
		}.ToJsonString();
	}

	/// <summary>ok=false with the valid options — the automation contract for an unknown car id.</summary>
	static string RejectCar( string car ) => new JsonObject
	{
		["ok"] = false,
		["error"] = $"unknown car '{car}' — valid cars: {string.Join( ", ", StationCarRegistry.KnownCarIds )}",
	}.ToJsonString();

	static string RejectStation( string station ) => new JsonObject
	{
		["ok"] = false,
		["error"] = $"unknown station '{station}' — valid stations: {string.Join( ", ", StationCarRegistry.ResolvableStationIds() )}"
			+ ( TestTrack.Stations is { Count: > 0 } ? "" : " (world not built yet — play_start in the proto world first)" ),
	}.ToJsonString();

	static string RejectManeuver( string man )
	{
		// crash is out of scope for this kit — a distinct, actionable message vs a plain unknown.
		if ( string.Equals( man?.Trim(), "crash", StringComparison.OrdinalIgnoreCase ) )
			return new JsonObject
			{
				["ok"] = false,
				["error"] = "maneuver 'crash' is out of scope for this kit (no destruction simulation)",
			}.ToJsonString();
		return new JsonObject
		{
			["ok"] = false,
			["error"] = $"unknown maneuver '{man}' — not registered in ManeuverRegistry",
		}.ToJsonString();
	}

	/// <summary>Drive control. <c>op</c> ∈ {status, spawn, maneuver, route, stop, reset}.
	/// <c>maneuver</c> kicks off a named scripted run and returns an ack; <c>status</c> returns the
	/// live telemetry report (contract §6.2). POST-and-poll: kick with maneuver, then poll status
	/// until <c>status:"done"</c>.</summary>
	/// <param name="argsJson">e.g. '{"op":"maneuver","maneuver":"launch","car":"hatch","station":"dragstrip","params":{"targetSpeedMs":27.78}}'
	/// or '{"op":"status"}'.</param>
	[McpTool( "vp_drive" )]
	public static string Drive( string argsJson = null )
	{
		var o = Parse( argsJson, out var err );
		if ( err != null ) return Error( err );

		string op = GetString( o, "op", "status" ).Trim().ToLowerInvariant();

		switch ( op )
		{
			case "status":
				return TelemetryJson();

			case "stop":
				VehicleBridge.Post( VehicleBridge.Op.Stop );
				return Ack( "stop posted" );

			case "reset":
				VehicleBridge.Post( VehicleBridge.Op.Reset );
				return Ack( "reset posted" );

			case "spawn":
				return Spawn( argsJson );

			case "maneuver":
			{
				if ( !VehicleBridge.DirectorAlive )
					return Error( "no play-mode pilot — call play_start first" );
				string man = GetString( o, "maneuver", "" );
				string car = GetString( o, "car", "hatch" );
				string station = GetString( o, "station", "" );
				// STRICT id validation before posting (audit 2026-07-13 MEDIUM): an ack must mean the
				// requested car/station/maneuver was valid. Unknown values fail closed with ok=false and
				// the valid options, rather than posting a run that only errors on a later fixed tick.
				if ( !StationCarRegistry.TryResolveCar( car, out _ ) )
					return RejectCar( car );
				if ( !string.IsNullOrEmpty( station ) && !VehiclePilot.ResolveStation( station, out _, out _ ) )
					return RejectStation( station );
				if ( ManeuverRegistry.Get( man ) is null )
					return RejectManeuver( man );
				var pars = ParseParams( o["params"] as JsonObject );
				int runId = VehicleBridge.Post( VehicleBridge.Op.Maneuver, car: car, station: station,
					maneuver: man, pars: pars );
				return new JsonObject
				{
					["ok"] = true,
					["runId"] = runId,
					["status"] = "running",
					["maneuver"] = man,
					["car"] = car,
					["station"] = station,
				}.ToJsonString();
			}

			case "route":
			{
				if ( !VehicleBridge.DirectorAlive )
					return Error( "no play-mode pilot — call play_start first" );
				string car = GetString( o, "car", "hatch" );
				string station = GetString( o, "station", "" );
				var route = ParseRoute( o["route"] as JsonArray );
				int runId = VehicleBridge.Post( VehicleBridge.Op.Route, car: car, station: station, route: route );
				return new JsonObject { ["ok"] = true, ["runId"] = runId, ["status"] = "running", ["waypoints"] = route.Length / 2 }.ToJsonString();
			}

			default:
				return Error( $"unknown op '{op}' (expected status|spawn|maneuver|route|stop|reset)" );
		}
	}

	/// <summary>Re-run the standing invariant audits on demand and return the greppable target-0
	/// lines (contract §6.1). Also logs them to the console.</summary>
	[McpTool( "vp_audit", Hints = McpToolHints.ReadOnly )]
	public static string Audit()
	{
		VehiclePilot.EmitAudits();
		return new JsonObject
		{
			["flips"] = VehicleBridge.Flips,
			["fallThroughs"] = VehicleBridge.FallThroughs,
			["stuckTicks"] = VehicleBridge.StuckTicks,
			["nanForces"] = VehicleBridge.NanForces,
			["sleepWhileDriving"] = VehicleBridge.SleepWhileDriving,
			["lines"] = new JsonArray
			{
				$"[vp] AUDIT flips offenders={VehicleBridge.Flips} target 0",
				$"[vp] AUDIT fallthroughs offenders={VehicleBridge.FallThroughs} target 0",
				$"[vp] AUDIT stuck offenders={VehicleBridge.StuckTicks} target 0",
				$"[vp] AUDIT nan_forces offenders={VehicleBridge.NanForces} target 0",
				$"[vp] AUDIT sleep_while_driving offenders={VehicleBridge.SleepWhileDriving} target 0",
			},
		}.ToJsonString();
	}

	/// <summary>PHASE-0 PERF baseline trigger (measurement-only). Drives the always-present, inert
	/// <see cref="PerfProbe"/> through the perf channel on <see cref="VehicleBridge"/> — the same
	/// post-and-poll idiom as <c>vp_drive</c>. Results are logged as greppable <c>[vp] PERF …</c>
	/// console lines (read them back with <c>read_console</c>); this tool returns an ack + a status
	/// poll so an automation loop knows when a capture window has finished.
	/// <para>Ops:</para>
	/// <list type="bullet">
	/// <item><c>{"op":"boot"}</c> — arm the one-shot world-build + kit-assembly timing for the NEXT
	/// play session (sets a game-assembly static; call BEFORE <c>play_start</c>). No capture.</item>
	/// <item><c>{"op":"census"}</c> — immediate one-shot scene census (play mode).</item>
	/// <item><c>{"op":"capture","mode":"idle"|"drive","seconds":30}</c> — run a fixed-window capture;
	/// "drive" applies a deterministic measurement weave through the car's input seam.</item>
	/// <item><c>{"op":"status"}</c> — poll the probe: idle | running | done, plus the last headline.</item>
	/// </list></summary>
	/// <param name="argsJson">e.g. '{"op":"capture","mode":"drive","seconds":30}'.</param>
	[McpTool( "vp_perf" )]
	public static string Perf( string argsJson = null )
	{
		var o = Parse( argsJson, out var err );
		if ( err != null ) return Error( err );

		string op = GetString( o, "op", "status" ).Trim().ToLowerInvariant();

		switch ( op )
		{
			case "boot":
				// Static write survives the editor→play boundary (same process, no assembly reload), so
				// arming here before play_start makes GameBootstrap time the upcoming build/spawn.
				GameBootstrap.PerfBoot = true;
				return new JsonObject
				{
					["ok"] = true,
					["armed"] = true,
					["note"] = "build+spawn timing armed for the next play_start; grep [vp] PERF BUILD / [vp] PERF KIT",
				}.ToJsonString();

			case "status":
				return new JsonObject
				{
					["ok"] = true,
					["perfStatus"] = VehicleBridge.PerfStatus,
					["mode"] = VehicleBridge.PerfMode,
					["lastAvgFps"] = VehicleBridge.PerfLastSummaryFps,
					["lastP1LowFps"] = VehicleBridge.PerfLastP1LowFps,
					["lastPhysAvgMs"] = VehicleBridge.PerfLastPhysAvgMs,
				}.ToJsonString();

			case "census":
			{
				if ( !VehicleBridge.DirectorAlive )
					return Error( "no play-mode probe — call play_start first" );
				int token = VehicleBridge.PostPerf( "census", 0f );
				return new JsonObject { ["ok"] = true, ["perfToken"] = token, ["op"] = "census" }.ToJsonString();
			}

			case "capture":
			{
				if ( !VehicleBridge.DirectorAlive )
					return Error( "no play-mode probe — call play_start first" );
				string mode = GetString( o, "mode", "idle" ).Trim().ToLowerInvariant();
				if ( mode != "idle" && mode != "drive" )
					return Error( $"unknown mode '{mode}' (expected idle|drive)" );
				float seconds = GetFloat( o, "seconds", 30f );
				int token = VehicleBridge.PostPerf( mode, seconds );
				return new JsonObject
				{
					["ok"] = true,
					["perfToken"] = token,
					["mode"] = mode,
					["seconds"] = seconds,
					["status"] = "running",
					["note"] = "poll {op:status} until perfStatus=done; numbers on the [vp] PERF console lines",
				}.ToJsonString();
			}

			default:
				return Error( $"unknown op '{op}' (expected boot|census|capture|status)" );
		}
	}

	/// <summary>Run the REAL <see cref="PartKitManifest.FromJson"/> (the shared parse→validate→
	/// normalize core of <see cref="PartKitManifest.TryLoad"/>) on a manifest and report accept/reject
	/// + the C# validator's own reasons (audit 2026-07-13 MEDIUM). Lets <c>tools/test_partkit.py --live</c>
	/// feed its 32 fixtures through the ACTUAL loader and catch any drift from the Python mirror —
	/// including the System.Text.Json binding class the Python reimplementation cannot see.
	/// Pass EITHER <c>json</c> (raw manifest text — preferred; no asset-mount timing) or <c>path</c>
	/// (mounted-fs path relative to Assets/).</summary>
	/// <param name="argsJson">e.g. '{"json":"{...manifest...}"}' or '{"path":"models/vehicles/hatch_kit/manifest.json"}'.</param>
	[McpTool( "vp_validate_manifest", Hints = McpToolHints.ReadOnly )]
	public static string ValidateManifest( string argsJson = null )
	{
		var o = Parse( argsJson, out var err );
		if ( err != null ) return Error( err );

		string json = GetString( o, "json", "" );
		string path = GetString( o, "path", "" );

		string text, label;
		if ( !string.IsNullOrEmpty( json ) )
		{
			text = json;
			label = "(inline json)";
		}
		else if ( !string.IsNullOrEmpty( path ) )
		{
			// Sandbox.FileSystem (the mounted game content) — fully qualified because the editor
			// assembly also has Editor.FileSystem (CS0104 ambiguity; caught only by the in-editor gate).
			if ( !Sandbox.FileSystem.Mounted.FileExists( path ) )
				return new JsonObject
				{
					["ok"] = true,
					["accept"] = false,
					["reasons"] = new JsonArray { $"not found in mounted fs: '{path}'" },
					["source"] = path,
				}.ToJsonString();
			text = Sandbox.FileSystem.Mounted.ReadAllText( path );
			label = path;
		}
		else
		{
			return Error( "give 'json' (raw manifest text) or 'path' (mounted-fs, relative to Assets/)" );
		}

		var manifest = PartKitManifest.FromJson( text, label, out var reasons );
		var arr = new JsonArray();
		foreach ( var r in reasons )
			arr.Add( r );

		return new JsonObject
		{
			["ok"] = true,
			["accept"] = manifest != null,
			["reasons"] = arr,
			["source"] = label,
		}.ToJsonString();
	}

	// ────────────────────────────── telemetry serialization ──────────────────────────────

	/// <summary>The frozen telemetry contract (docs/testing-harness.md §6.2) — EXACT camelCase.</summary>
	static string TelemetryJson() => new JsonObject
	{
		// envelope
		["status"] = VehicleBridge.Status,
		["maneuver"] = VehicleBridge.Maneuver,
		["car"] = VehicleBridge.Car,
		["station"] = VehicleBridge.Station,
		["runId"] = VehicleBridge.RunId,
		["message"] = VehicleBridge.Message,
		// metrics
		["speedMs"] = VehicleBridge.SpeedMs,
		["maxSpeedMs"] = VehicleBridge.MaxSpeedMs,
		["distanceM"] = VehicleBridge.DistanceM,
		["elapsedS"] = VehicleBridge.ElapsedS,
		["lateralGPeak"] = VehicleBridge.LateralGPeak,
		["lateralGAvg"] = VehicleBridge.LateralGAvg,
		["longGPeak"] = VehicleBridge.LongGPeak,
		["longGAvg"] = VehicleBridge.LongGAvg,
		["yawRatePeakDeg"] = VehicleBridge.YawRatePeakDeg,
		["yawRateAvgDeg"] = VehicleBridge.YawRateAvgDeg,
		["pitchDeg"] = VehicleBridge.PitchDeg,
		["rollDeg"] = VehicleBridge.RollDeg,
		["headingDriftDeg"] = VehicleBridge.HeadingDriftDeg,
		["gear"] = VehicleBridge.Gear,
		["gearAtVmax"] = VehicleBridge.GearAtVmax,
		["rpm"] = VehicleBridge.Rpm,
		["wheelspinS"] = VehicleBridge.WheelspinS,
		["zeroToHundredS"] = VehicleBridge.ZeroToHundredS,
		["brakeDistanceM"] = VehicleBridge.BrakeDistanceM,
		["lockupTicks"] = VehicleBridge.LockupTicks,
		["airtimeS"] = VehicleBridge.AirtimeS,
		["landingPitchDeg"] = VehicleBridge.LandingPitchDeg,
		["landingRollDeg"] = VehicleBridge.LandingRollDeg,
		["settleS"] = VehicleBridge.SettleS,
		["jturnTimeS"] = VehicleBridge.JturnTimeS,
		["yawOvershootDeg"] = VehicleBridge.YawOvershootDeg,
		["coneStrikes"] = VehicleBridge.ConeStrikes,
		["contactLossPct"] = VehicleBridge.ContactLossPct,
		["wheelContactLossPct"] = VehicleBridge.WheelContactLossPct,
		["rollbackM"] = VehicleBridge.RollbackM,
		["exitRecoveryS"] = VehicleBridge.ExitRecoveryS,
		["speedRetention"] = VehicleBridge.SpeedRetention,
		["peakSlipDeg"] = VehicleBridge.PeakSlipDeg,
		["recoveryS"] = VehicleBridge.RecoveryS,
		// standing audits (target 0)
		["flips"] = VehicleBridge.Flips,
		["flippedTicks"] = VehicleBridge.FlippedTicks,
		["fallThroughs"] = VehicleBridge.FallThroughs,
		["stuckTicks"] = VehicleBridge.StuckTicks,
		["nanForces"] = VehicleBridge.NanForces,
		["sleepWhileDriving"] = VehicleBridge.SleepWhileDriving,
		// booleans
		["catchable"] = VehicleBridge.Catchable,
		["spunOut"] = VehicleBridge.SpunOut,
		["climbed"] = VehicleBridge.Climbed,
	}.ToJsonString();

	// ────────────────────────────── json helpers ──────────────────────────────

	static JsonObject Parse( string argsJson, out string error )
	{
		error = null;
		if ( string.IsNullOrWhiteSpace( argsJson ) )
			return new JsonObject();
		try
		{
			return JsonNode.Parse( argsJson ) as JsonObject ?? new JsonObject();
		}
		catch ( Exception e )
		{
			error = $"bad argsJson: {e.Message}";
			return null;
		}
	}

	static IReadOnlyDictionary<string, float> ParseParams( JsonObject o )
	{
		if ( o is null ) return null;
		var d = new Dictionary<string, float>( StringComparer.OrdinalIgnoreCase );
		foreach ( var kv in o )
		{
			try
			{
				d[kv.Key] = kv.Value switch
				{
					JsonValue v when v.TryGetValue<double>( out var num ) => (float)num,
					JsonValue v when v.TryGetValue<bool>( out var b ) => b ? 1f : 0f,
					_ => 0f,
				};
			}
			catch { /* skip un-coercible values */ }
		}
		return d;
	}

	static float[] ParseRoute( JsonArray arr )
	{
		if ( arr is null ) return Array.Empty<float>();
		var flat = new List<float>();
		foreach ( var n in arr )
		{
			if ( n is not JsonObject w ) continue;
			flat.Add( GetFloat( w, "x", 0f ) );
			flat.Add( GetFloat( w, "y", 0f ) );
		}
		return flat.ToArray();
	}

	static string Ack( string msg ) => new JsonObject { ["ok"] = true, ["msg"] = msg }.ToJsonString();
	static string Error( string msg ) => new JsonObject { ["error"] = msg }.ToJsonString();

	static string GetString( JsonObject o, string key, string fallback )
	{
		var v = o?[key];
		if ( v is null ) return fallback;
		try { return v.GetValue<string>(); }
		catch { return fallback; }
	}

	static float GetFloat( JsonObject o, string key, float fallback )
	{
		var v = o?[key];
		if ( v is null ) return fallback;
		try { return (float)v.AsValue().GetValue<double>(); }
		catch { return fallback; }
	}
}
