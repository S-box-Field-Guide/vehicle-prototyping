namespace VehicleProto;

/// <summary>
/// Per-run/per-tick context handed to an <see cref="IManeuver"/>. Bundles the controlled car, its
/// rigidbody, the live elapsed run time, the parsed spec params, and the shared
/// <see cref="TelemetryAccumulator"/> (maneuvers read cumulative yaw / contact-loss / landing state
/// from it rather than re-deriving), plus the <see cref="Drive"/> input-staging helper and heading
/// math. A maneuver NEVER applies forces — it only stages <see cref="DriveInputs"/> through
/// <see cref="Drive"/>, driving the exact input path a player uses (the same input-staging
/// pattern), exactly as the monolithic pilot did.
/// </summary>
public sealed class ManeuverContext
{
	public VehicleController Car { get; }
	public Rigidbody Body { get; }
	public TelemetryAccumulator Telemetry { get; }

	/// <summary>Seconds since the maneuver started (the pilot's live run timer).</summary>
	public float RunTime { get; internal set; }

	IReadOnlyDictionary<string, float> _params;

	public ManeuverContext( VehicleController car, Rigidbody body, TelemetryAccumulator telemetry )
	{
		Car = car;
		Body = body;
		Telemetry = telemetry;
	}

	public void SetParams( IReadOnlyDictionary<string, float> pars ) => _params = pars;

	public float Param( string key, float fallback )
		=> _params != null && _params.TryGetValue( key, out var v ) ? v : fallback;

	/// <summary>Stage driver intent for this tick through the controller's input-override seam.</summary>
	public void Drive( float forward, float steer, bool handbrake )
	{
		Car.InputOverride = new DriveInputs
		{
			MoveForward = Math.Clamp( forward, -1f, 1f ),
			Steer = Math.Clamp( steer, -1f, 1f ),
			Handbrake = handbrake,
		};
	}

	public float HeadingDeg() => TelemetryAccumulator.HeadingDeg( Car );
}
