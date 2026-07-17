namespace VehicleProto;

/// <summary>
/// Play-mode test director. Created by <see cref="GameBootstrap"/>. Each
/// FixedUpdate it (1) consumes any command posted through <see cref="VehicleBridge"/> by the editor
/// McpTools, and (2) advances the running scripted maneuver — injecting synthetic
/// <see cref="DriveInputs"/> into the active <see cref="VehicleController"/> through its
/// <see cref="VehicleController.InputOverride"/> seam (the pilot never applies forces itself; it
/// drives the exact input path a player uses).
///
/// This class is now COMMAND CONSUMPTION + SUBSYSTEM SEQUENCING only.
/// The concerns it used to inline now live in their own files: station/car resolution
/// (<see cref="StationCarRegistry"/>), per-tick telemetry (<see cref="TelemetryAccumulator"/>),
/// standing audits (<see cref="InvariantAuditAccumulator"/>), each scripted maneuver
/// (<see cref="IManeuver"/> / <see cref="ManeuverRegistry"/>), and the run verdict shared with the
/// UI (<see cref="RunVerdict"/>). The static <c>ResolveCar</c>/<c>ResolveStation</c>/<c>SeatHeightM</c>
/// /<c>EmitAudits</c> members stay here as the stable public seam that VpTools, CarSwitcher, and the
/// part-kit commands call.
///
/// It also honours the <c>vp_pilot</c> ConVar on-ramp so the battery can be kicked without MCP.
/// </summary>
public sealed class VehiclePilot : Component
{
	public VehicleController ActiveCar { get; set; }
	Rigidbody _rb;

	// ── run state ──
	bool _running;
	string _man = "";
	float _runTime;
	float _maxRunS = 20f;
	IReadOnlyDictionary<string, float> _params;
	AssistLevel? _assistOverride; // per-maneuver assist pin; null = car's DefaultAssists
	string _lastRunLine = "";     // exact "[vp] RUN ..." text, mirrored into UiFeed.LastRunLine

	// ── extracted concerns ──
	readonly TelemetryAccumulator _telemetry = new();
	readonly InvariantAuditAccumulator _audits = new();
	IManeuver _activeManeuver;
	ManeuverContext _ctx;

	// ── ConVar on-ramp ──
	// e.g. `vp_pilot launch` in the console auto-runs that maneuver on the hatch at its station.
	[ConVar( "vp_pilot" )] public static string PilotConVar { get; set; } = "";
	string _lastConVar = "";

	protected override void OnEnabled()
	{
		VehicleBridge.DirectorAlive = true;
	}

	protected override void OnDisabled()
	{
		VehicleBridge.DirectorAlive = false;
		if ( ActiveCar.IsValid() )
			ActiveCar.InputOverride = null;
	}

	protected override void OnFixedUpdate()
	{
		// ConVar on-ramp: a non-empty change posts a maneuver command (single-player, no MCP).
		if ( PilotConVar != _lastConVar )
		{
			_lastConVar = PilotConVar;
			if ( !string.IsNullOrWhiteSpace( PilotConVar ) )
			{
				var man = PilotConVar.Trim().ToLowerInvariant();
				VehicleBridge.Post( VehicleBridge.Op.Maneuver, car: "hatch",
					station: StationCarRegistry.DefaultStationFor( man ), maneuver: man );
				Log.Info( $"[vp] vp_pilot convar -> maneuver {man}" );
			}
		}

		if ( VehicleBridge.HasPending )
			ConsumeCommand();

		if ( _running )
			TickRun();
	}

	// ────────────────────────────── command intake ──────────────────────────────

	void ConsumeCommand()
	{
		var op = VehicleBridge.PendingOp;
		var car = VehicleBridge.CmdCar;
		var station = VehicleBridge.CmdStation;
		VehicleBridge.MarkConsumed();

		switch ( op )
		{
			case VehicleBridge.Op.Spawn:
				SpawnCarAt( car, station );
				break;

			case VehicleBridge.Op.Maneuver:
				if ( !string.IsNullOrEmpty( station ) )
					SpawnCarAt( car, station );
				StartManeuver( VehicleBridge.CmdManeuver, car, station, VehicleBridge.CmdParams );
				break;

			case VehicleBridge.Op.Route:
				// Waypoint following is a first-class maneuver object (RouteManeuver) that stubs out
				// for now — seat the car and run it like any maneuver so the loop never hangs; the real
				// follower drops into RouteManeuver.Tick later, no pilot change.
				if ( !string.IsNullOrEmpty( station ) )
					SpawnCarAt( car, station );
				StartManeuver( "route", car, station, VehicleBridge.CmdParams );
				break;

			case VehicleBridge.Op.Stop:
			case VehicleBridge.Op.Reset:
				StopRun();
				break;
		}
	}

	// ────────────────────────────── spawning ──────────────────────────────

	void SpawnCarAt( string carId, string station )
	{
		var def = StationCarRegistry.ResolveCar( carId );

		Vector3 posM;
		Rotation facing;
		bool resolvedStation = StationCarRegistry.ResolveStation( station, out posM, out facing );
		if ( !resolvedStation )
		{
			// no/unknown station: reset in place at the current car's spawn-ish position
			posM = ActiveCar.IsValid() ? ActiveCar.WorldPosition * Units.UnitsToMeters : Vector3.Zero;
			facing = ActiveCar.IsValid() ? ActiveCar.WorldRotation : Rotation.Identity;
		}

		if ( ActiveCar.IsValid() )
			ActiveCar.GameObject.Destroy();

		float m = Units.MetersToUnits;
		float seatZM = SeatHeightM( def );
		var pos = new Vector3( posM.x, posM.y, posM.z ) * m + Vector3.Up * seatZM * m;

		var carGo = VehicleFactory.Spawn( Scene, def, pos, facing );
		ActiveCar = carGo.Components.Get<VehicleController>();
		_rb = carGo.Components.Get<Rigidbody>();
		VehicleBridge.SpawnedCar = carId;

		// keep the chase camera on the car under test so screenshots frame it
		var camGo = Scene.Camera?.GameObject;
		var chase = camGo?.Components.Get<VehicleCamera>();
		if ( chase is not null )
			chase.Target = ActiveCar;

		// Re-point the whole HUD stack at the freshly spawned car, exactly like the live world-switch
		// (GameBootstrap.ApplyWorld) and car-swap (CarSwitcher.SwitchTo) paths already do. Without this
		// the HUD chip / RPM band / assist readout / tuning panel keep reading the DESTROYED previous
		// controller, so after a world switch or car swap the on-screen identity disagrees with the body
		// and physics that were actually spawned (readout-vs-body divergence).
		UiRig.Retarget( Scene, ActiveCar );

		Log.Info( $"[vp] spawn car={carId} station={station} seatZ={seatZM:F2}m at ({posM.x:F0},{posM.y:F0})" );
	}

	/// <summary>Suspension-equilibrium seat height above the station ground (SI m). Springs already
	/// carry the weight at rest — NOT surface+radius, which flings the car on spawn.
	/// Public seam: VpTools, CarSwitcher, WallGrazeProbe, and the part-kit commands call this.</summary>
	public static float SeatHeightM( CarDefinition def )
	{
		// gravity was set explicitly at boot (~1.1 g); read it live so the math tracks the scene.
		float gravity = Game.ActiveScene?.PhysicsWorld is { } pw
			? pw.Gravity.Length * Units.UnitsToMeters
			: 9.81f * 1.1f;
		float staticCompression = def.Mass * gravity / 4f / def.SpringRate;
		return def.SuspensionTravel + def.WheelRadius - staticCompression + def.RideHeight;
	}

	// ────────────────────────────── run lifecycle ──────────────────────────────

	void StartManeuver( string man, string car, string station, IReadOnlyDictionary<string, float> pars )
	{
		if ( !ActiveCar.IsValid() )
		{
			VehicleBridge.ResetRun( man, car, station );
			FailRun( "no active car to run maneuver on" );
			return;
		}

		_rb = ActiveCar.Components.Get<Rigidbody>(); // ensure bound (e.g. initial city car)

		_man = (man ?? "").Trim().ToLowerInvariant();
		_params = pars;
		_maxRunS = Param( "maxRunS", 20f );

		// per-maneuver assist policy (docs/testing-harness.md §7): a spec may pin an assist level
		// for a run via the "assist" param (0=Casual, 1=Sport, 2=Sim). Held as pilot state and
		// re-applied every TickRun because the freshly-spawned car's OnStart (which sets
		// Assists = DefaultAssists) runs AFTER this StartManeuver in the same command tick.
		_assistOverride = null;
		if ( _params != null && _params.TryGetValue( "assist", out var aLvl ) )
			_assistOverride = (int)MathF.Round( aLvl ) switch
			{
				1 => AssistLevel.Sport,
				2 => AssistLevel.Sim,
				_ => AssistLevel.Casual,
			};

		_running = true;
		_runTime = 0f;

		_activeManeuver = ManeuverRegistry.Get( _man );
		_telemetry.Start( ActiveCar, _rb );
		_audits.Start();
		_ctx = new ManeuverContext( ActiveCar, _rb, _telemetry );
		_ctx.SetParams( _params );
		_activeManeuver?.Start( _ctx );

		VehicleBridge.ResetRun( _man, car, station );
		UiFeed_OnManeuverStart( _man, car, station );
		Log.Info( $"[vp] RUN {_man} car={car} station={station} start" );
	}

	void StopRun()
	{
		_running = false;
		if ( ActiveCar.IsValid() )
			ActiveCar.InputOverride = null;
		if ( VehicleBridge.Status == "running" )
			VehicleBridge.Status = "idle";
	}

	/// <summary>Operational-failure exit (audit 2026-07-12 MEDIUM): reports status="error"; telemetry
	/// fields are NOT a measurement on this path. A maxRunS TIMEOUT deliberately stays a FinishRun
	/// ("done") — its telemetry is real measured DNF data (documented in docs/testing-harness.md §7.3).</summary>
	void FailRun( string message )
	{
		_running = false;
		if ( ActiveCar.IsValid() )
			ActiveCar.InputOverride = new DriveInputs { MoveForward = -1f, Steer = 0f, Handbrake = true };

		VehicleBridge.Status = "error";
		VehicleBridge.Message = message;
		VehicleBridge.ElapsedS = _runTime;
		Log.Info( $"[vp] RUN {_man} car={VehicleBridge.Car} ERROR {message}" );
		InvariantAuditAccumulator.Emit();

		// keep the last GOOD run-line on the card (FailRun never emitted a done line, same as before)
		var verdict = RunVerdict.Build( _man, message, _activeManeuver );
		verdict.Line = _lastRunLine;
		UiFeed_OnRunFinished( verdict );
	}

	void FinishRun( string message = "" )
	{
		_running = false;
		// POST-RUN BRAKE-HOLD (coast defect): latch a full-brake + handbrake hold so the
		// finished car parks where it stopped instead of coasting off the finite station pad. An
		// explicit Stop/Reset still releases to null so the car can be hand-driven between runs.
		if ( ActiveCar.IsValid() )
			ActiveCar.InputOverride = new DriveInputs { MoveForward = -1f, Steer = 0f, Handbrake = true };

		VehicleBridge.Status = "done";
		VehicleBridge.Message = message;
		VehicleBridge.ElapsedS = _runTime;
		_telemetry.Finish(); // per-run averages (LateralGAvg / YawRateAvgDeg / ContactLossPct)

		var verdict = RunVerdict.Build( _man, message, _activeManeuver );
		_lastRunLine = verdict.Line;
		Log.Info( _lastRunLine );
		InvariantAuditAccumulator.Emit();
		UiFeed_OnRunFinished( verdict );
	}

	// ────────────────────────────── per-tick ──────────────────────────────

	void TickRun()
	{
		if ( !ActiveCar.IsValid() || !_rb.IsValid() )
		{
			FailRun( "active car went invalid mid-run" );
			return;
		}

		float dt = Time.Delta;
		_runTime += dt;
		_ctx.RunTime = _runTime;

		// re-assert the per-maneuver assist pin every tick — the car's OnStart resets Assists to
		// DefaultAssists a frame or two into the run, so a one-shot set loses.
		if ( ActiveCar.Definition != null )
			ActiveCar.Assists = _assistOverride ?? ActiveCar.Definition.DefaultAssists;

		_telemetry.Accumulate( ActiveCar, _rb, _runTime, dt );
		_audits.Accumulate( ActiveCar, _rb, dt );
		UiFeed_OnTick();

		if ( _activeManeuver is null )
		{
			FailRun( $"unknown maneuver '{_man}'" );
			return;
		}

		bool done = _activeManeuver.Tick( _ctx, dt );

		if ( done || _runTime >= _maxRunS )
		{
			if ( !done )
				VehicleBridge.Message = $"maxRunS ({_maxRunS:F0}s) reached";
			FinishRun( VehicleBridge.Message );
		}
	}

	// ────────────────────────────── static seams (external callers) ──────────────────────────────

	/// <summary>Re-run the standing invariant audits (VpTools vp_audit calls this).</summary>
	public static void EmitAudits() => InvariantAuditAccumulator.Emit();

	/// <summary>Public station/car resolution seam — forwards to <see cref="StationCarRegistry"/>
	/// (VpTools, CarSwitcher, PartKitCommands call these).</summary>
	public static CarDefinition ResolveCar( string id ) => StationCarRegistry.ResolveCar( id );

	public static bool ResolveStation( string id, out Vector3 posMeters, out Rotation facing )
		=> StationCarRegistry.ResolveStation( id, out posMeters, out facing );

	float Param( string key, float fallback )
		=> _params != null && _params.TryGetValue( key, out var v ) ? v : fallback;

	// ────────────────────────────── UiFeed timing widget ──────────────
	// The pilot is the ONLY writer into UiFeed. The verdict CARD is projected by RunVerdict (step 4 of
	// the decomposition — one DTO feeds the console line and the card). These hooks own only the live
	// timing widget + per-session best-times, which are run-lifecycle state, not verdict data.

	static readonly HashSet<string> TimedManeuvers = new( StringComparer.OrdinalIgnoreCase ) { "launch", "brake" };

	// per-session best (station -> seconds for launch, meters for brake); per-station, not per-car.
	static readonly Dictionary<string, float> _bestByStation = new( StringComparer.OrdinalIgnoreCase );

	void UiFeed_OnManeuverStart( string man, string car, string station )
	{
		if ( !TimedManeuvers.Contains( man ) )
		{
			UiFeed.TimingRunning = false;
			return;
		}

		string key = BestKey( man, station );
		UiFeed.TimingTitle = man == "launch" ? "0–100 km/h" : "100–0 distance";
		UiFeed.TimingUnit = man == "launch" ? "s" : "m";
		UiFeed.TimingContext = $"{man} · run {VehicleBridge.RunId}";
		UiFeed.TimingRunning = true;
		UiFeed.TimingValue = "—";
		UiFeed.TimingBest = _bestByStation.TryGetValue( key, out var best )
			? $"best {best:F2}"
			: "";
	}

	void UiFeed_OnTick()
	{
		var tv = _activeManeuver?.TimingValue( _ctx );
		if ( tv != null )
			UiFeed.TimingValue = tv;
	}

	void UiFeed_OnRunFinished( RunVerdict verdict )
	{
		UiFeed.TimingRunning = false;

		if ( TimedManeuvers.Contains( _man ) )
		{
			float result = _man == "launch" ? VehicleBridge.ZeroToHundredS : VehicleBridge.BrakeDistanceM;
			UiFeed.TimingValue = result > 0f ? result.ToString( _man == "launch" ? "F2" : "F1" ) : "—";

			if ( result > 0f )
			{
				string key = BestKey( _man, VehicleBridge.Station );
				if ( !_bestByStation.TryGetValue( key, out var best ) || result < best )
					_bestByStation[key] = result;

				UiFeed.TimingBest = $"best {_bestByStation[key]:F2}";
			}
		}

		verdict.ApplyToUiFeed( verdict.Car );
	}

	static string BestKey( string man, string station )
		=> string.IsNullOrEmpty( station ) ? man : station;
}
