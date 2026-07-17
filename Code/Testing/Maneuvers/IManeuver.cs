namespace VehicleProto;

/// <summary>
/// One scripted maneuver (the pilot decomposition). Each concrete
/// maneuver is a self-contained object that owns (1) its per-run phase state, (2) its drive profile
/// (<see cref="Tick"/> stages <see cref="DriveInputs"/> through the context — it never applies
/// forces), and (3) its own telemetry projection (<see cref="Report"/> — both the console log token
/// and the card row for each metric). Adding a maneuver is therefore LOCAL: a new class here, one
/// entry in <see cref="ManeuverRegistry"/>, and a spec file (docs/testing-harness.md §8).
/// </summary>
public interface IManeuver
{
	/// <summary>The battery name (matches the spec's "maneuver" field and DriveManeuver key).</summary>
	string Name { get; }

	/// <summary>Reset this maneuver's per-run state at the start of a run.</summary>
	void Start( ManeuverContext ctx );

	/// <summary>Drive one tick by staging <see cref="ManeuverContext.Drive"/>. Return true when the
	/// maneuver's OWN completion condition is met (else the run ends on maxRunS).</summary>
	bool Tick( ManeuverContext ctx, float dt );

	/// <summary>Fill the verdict's metric rows from <see cref="VehicleBridge"/> (the maneuver's frozen
	/// telemetry fields). This is the SINGLE definition the console run-line and the card both use.</summary>
	void Report( RunVerdict verdict );

	/// <summary>Live value for the top-center timing widget while a run is in progress; null = this
	/// maneuver isn't shown as a timed run (only launch/brake are).</summary>
	string TimingValue( ManeuverContext ctx ) => null;
}

/// <summary>Shared base for maneuvers that need the accel-plateau detector (launch, top speed).
/// A car whose acceleration flattens for a ~2 s window is "topped out" — used to end launch/topspeed
/// runs instead of idling at full throttle to maxRunS. State is per-instance and re-armed by
/// <see cref="ResetPlateau"/> each run (the registry caches one instance per maneuver).</summary>
public abstract class ManeuverBase : IManeuver
{
	public abstract string Name { get; }
	public abstract void Start( ManeuverContext ctx );
	public abstract bool Tick( ManeuverContext ctx, float dt );
	public abstract void Report( RunVerdict verdict );
	public virtual string TimingValue( ManeuverContext ctx ) => null;

	bool _plateauMark;
	float _plateauTime;
	float _plateauSpeed;

	protected void ResetPlateau() => _plateauMark = false;

	protected bool AccelStalled( float runTime )
	{
		if ( !_plateauMark )
		{
			_plateauMark = true;
			_plateauTime = runTime;
			_plateauSpeed = VehicleBridge.SpeedMs;
			return false;
		}
		if ( runTime - _plateauTime < 2f )
			return false;
		bool stalled = VehicleBridge.SpeedMs - _plateauSpeed < 0.5f;
		_plateauTime = runTime;
		_plateauSpeed = VehicleBridge.SpeedMs;
		return stalled;
	}
}
