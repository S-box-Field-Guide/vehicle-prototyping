namespace VehicleProto;

/// <summary>
/// Live 0–100 km/h (0–60 mph) launch timer for the PLAYER, feeding the TelemetryPanel's
/// top-center TIMING card during ordinary free driving. Mounted alongside the always-on HUD
/// pieces by <see cref="UiRig.Mount"/> and re-pointed by <see cref="UiRig.Retarget"/>, exactly
/// like the panels — its <see cref="Target"/> follows the active car.
///
/// A tiny state machine on the active car:
///   ARMED    — near-standstill (|speed| &lt; 0.3 m/s) with no throttle; card shows the metric,
///              a dash, and the session best.
///   RUNNING  — first movement under throttle starts the clock; live elapsed streams into the
///              card. The 0→target split is captured when speed crosses the target (27.78 m/s for
///              km/h, 26.82 m/s for mph); the ¼-mile (402.336 m) split fills the footer row.
///   done     — split captured (and ¼ mile, or the max window elapsed): value freezes at the
///              split, best updates.
///   abort    — brake-to-stop before reaching the target, respawn, or car switch → re-arm.
///
/// HARNESS PRECEDENCE: while a scripted pilot maneuver is running
/// (<see cref="VehicleBridge.Status"/> == "running"), this feeder goes dormant and does NOT
/// touch UiFeed — automated runs keep exactly their current display. It reads that existing
/// signal only; it adds no coupling into <c>Code/Testing/</c>.
///
/// All internals are SI (m/s, m); only the target constant + title/format switch on
/// <see cref="UserSettings.SpeedUnit"/>. Best times are in-memory statics keyed by car name,
/// mirroring VehiclePilot's <c>_bestByStation</c> — they last the play session, not the disk.
/// </summary>
public sealed class PlayerTiming : Component
{
	public VehicleController Target { get; set; }

	// ── thresholds (SI) ──
	const float ArmSpeedMs      = 0.3f;    // |speed| below this = standstill (arm / stop)
	const float ArmThrottle     = 0.05f;   // throttle below this = "no throttle" when arming
	const float TargetKmhMs     = 27.78f;  // 100 km/h
	const float TargetMphMs     = 26.82f;  // 60 mph (26.8224)
	const float QuarterMileM    = 402.336f;
	const float MaxRunS         = 30f;     // safety cap so a car that can't reach the split re-arms

	enum Phase { Armed, Running }
	Phase _phase = Phase.Armed;

	VehicleController _lastTarget;   // reference change = respawn / car switch → abort
	bool _wrote;                     // have we owned the card since the last harness handoff?

	// ── run state ──
	float _elapsed;
	float _distM;
	Vector3 _lastPosM;
	bool _splitCaptured;
	bool _quarterCaptured;
	float _splitS;
	float _quarterS;
	float _targetMs;                 // sampled at run start (unit is fixed for the run)
	string _title = "0–100 KM/H";

	// ── per-session best, per car per metric (mirrors VehiclePilot._bestByStation) ──
	static readonly Dictionary<string, float> _bestSplit   = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, float> _bestQuarter = new( StringComparer.OrdinalIgnoreCase );

	protected override void OnFixedUpdate()
	{
		// Harness owns the card during a scripted maneuver — stay fully dormant and DON'T write UiFeed.
		if ( VehicleBridge.Status == "running" )
		{
			_phase = Phase.Armed;
			_wrote = false;               // re-assert the armed card when free-drive resumes
			return;
		}

		if ( !Target.IsValid() )
			return;

		// Respawn / car swap: Retarget hands us a brand-new controller instance → abort any run.
		if ( !ReferenceEquals( Target, _lastTarget ) )
		{
			_lastTarget = Target;
			ResetToArmed();
		}

		float speed = Target.SpeedMs;
		float throttle = Target.Throttle;
		Vector3 posM = Target.WorldPosition * Units.UnitsToMeters;

		if ( _phase == Phase.Running )
			TickRun( speed, posM );
		else
			TickArmed( speed, throttle, posM );
	}

	void TickArmed( float speed, float throttle, Vector3 posM )
	{
		bool eligible = speed < ArmSpeedMs && throttle < ArmThrottle;

		// Refresh the armed card once (title/best can change with the chosen unit or a new best).
		if ( !_wrote || eligible )
			WriteArmed();

		// Launch: from a standstill-armed state, the first real movement under throttle starts the clock.
		if ( eligible ) return;                                   // still stationary, keep armed
		if ( speed >= ArmSpeedMs && throttle > ArmThrottle )
			BeginRun( posM );
	}

	void BeginRun( Vector3 posM )
	{
		_phase = Phase.Running;
		_elapsed = 0f;
		_distM = 0f;
		_lastPosM = posM;
		_splitCaptured = _quarterCaptured = false;
		_splitS = _quarterS = 0f;

		bool mph = UserSettings.SpeedUnit == SpeedUnit.Mph;
		_targetMs = mph ? TargetMphMs : TargetKmhMs;
		_title = mph ? "0–60 MPH" : "0–100 KM/H";

		UiFeed.TimingTitle = _title;
		UiFeed.TimingUnit = "s";
		UiFeed.TimingContext = "launch · free drive";
		UiFeed.TimingRunning = true;
		UiFeed.TimingValue = "0.00";
		UiFeed.TimingExtra1 = "¼ mile —";
		UiFeed.TimingBest = BestSplitLabel();
		_wrote = true;
	}

	void TickRun( float speed, Vector3 posM )
	{
		_elapsed += Time.Delta;

		// horizontal distance travelled (planar; ignore ride-height bob)
		var d = posM - _lastPosM;
		_distM += new Vector3( d.x, d.y, 0f ).Length;
		_lastPosM = posM;

		// abort before the split lands: rolled to a stop, or the window ran out.
		if ( !_splitCaptured && (speed < ArmSpeedMs || _elapsed > MaxRunS) )
		{
			ResetToArmed();
			return;
		}

		if ( !_splitCaptured && speed >= _targetMs )
		{
			_splitCaptured = true;
			_splitS = _elapsed;
			RecordBest( _bestSplit, _splitS );
			UiFeed.TimingBest = BestSplitLabel();
		}

		if ( !_quarterCaptured && _distM >= QuarterMileM )
		{
			_quarterCaptured = true;
			_quarterS = _elapsed;
			RecordBest( _bestQuarter, _quarterS );
		}

		// live value: streaming elapsed until the split, then frozen at the split time.
		UiFeed.TimingValue = (_splitCaptured ? _splitS : _elapsed).ToString( "F2" );
		UiFeed.TimingExtra1 = _quarterCaptured ? $"¼ mile {_quarterS:F2}" : "¼ mile —";
		UiFeed.TimingRunning = true;

		// complete once both splits are in, the car stops, or the window closes.
		bool done = (_splitCaptured && _quarterCaptured)
			|| (_splitCaptured && (speed < ArmSpeedMs || _elapsed > MaxRunS));
		if ( done )
		{
			UiFeed.TimingRunning = false;
			_phase = Phase.Armed;           // ready for the next launch; card keeps the completed value
			_wrote = true;
		}
	}

	void WriteArmed()
	{
		bool mph = UserSettings.SpeedUnit == SpeedUnit.Mph;
		_title = mph ? "0–60 MPH" : "0–100 KM/H";

		UiFeed.TimingTitle = _title;
		UiFeed.TimingUnit = "s";
		UiFeed.TimingContext = "launch · free drive";
		UiFeed.TimingRunning = false;
		UiFeed.TimingValue = "—";
		UiFeed.TimingExtra1 = "¼ mile —";
		UiFeed.TimingBest = BestSplitLabel();
		_wrote = true;
	}

	void ResetToArmed()
	{
		_phase = Phase.Armed;
		_elapsed = 0f;
		_distM = 0f;
		_splitCaptured = _quarterCaptured = false;
		WriteArmed();
	}

	string CarKey => Target?.Definition?.Name ?? "car";

	string BestSplitLabel()
		=> _bestSplit.TryGetValue( CarKey, out var b ) ? $"best {b:F2}" : "";

	void RecordBest( Dictionary<string, float> best, float value )
	{
		if ( value <= 0f ) return;
		string key = CarKey;
		if ( !best.TryGetValue( key, out var cur ) || value < cur )
			best[key] = value;
	}
}
