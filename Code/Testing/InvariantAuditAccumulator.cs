namespace VehicleProto;

/// <summary>
/// Owns the standing-invariant audit state + math extracted from <see cref="VehiclePilot"/>
/// (the pilot decomposition). Each fixed tick the pilot calls
/// <see cref="Accumulate"/>, which counts the target-0 offenders into <see cref="VehicleBridge"/>
/// exactly as the old <c>AccumulateAudits</c> did: flips (sustained-inversion events + inverted
/// ticks), fall-throughs, stuck-under-throttle, NaN solver state, sleep-while-driving.
/// <see cref="Emit"/> logs the greppable <c>[vp] AUDIT &lt;name&gt; offenders=N target 0</c> lines
/// (re-exported by <see cref="VehiclePilot.EmitAudits"/>, which VpTools calls). PURE MOVE.
/// </summary>
public sealed class InvariantAuditAccumulator
{
	bool _flippedNow;

	/// <summary>Reset the per-run edge state (the pilot object outlives a run).</summary>
	public void Start() => _flippedNow = false;

	public void Accumulate( VehicleController car, Rigidbody rb, float dt )
	{
		var posM = car.WorldPosition * Units.UnitsToMeters;
		var up = car.WorldRotation.Up;

		// flips: inverted body. count sustained events, plus every inverted tick.
		bool inverted = up.z < 0f;
		if ( inverted )
		{
			VehicleBridge.FlippedTicks++;
			if ( !_flippedNow ) { _flippedNow = true; VehicleBridge.Flips++; }
		}
		else _flippedNow = false;

		// fall-through: dropped well below the ground plane (world floor ~0 m). one event.
		if ( posM.z < -5f && VehicleBridge.FallThroughs == 0 )
			VehicleBridge.FallThroughs++;

		// stuck: throttle applied, grounded, no forward progress
		if ( car.Throttle > 0.3f && VehicleBridge.SpeedMs < 0.3f
			&& car.Wheels.Count( w => w.IsGrounded ) >= 3 )
			VehicleBridge.StuckTicks++;

		// NaN in the solver state
		if ( IsNan( posM ) || IsNan( rb.Velocity ) )
			VehicleBridge.NanForces++;

		// sleep-while-driving: AutoSleep is disabled, so a sleeping body under drive is a real bug
		if ( car.Throttle > 0.3f && IsBodyAsleep( rb ) )
			VehicleBridge.SleepWhileDriving++;
	}

	/// <summary>Emit the standing invariant audits (greppable, target 0).</summary>
	public static void Emit()
	{
		Log.Info( $"[vp] AUDIT flips offenders={VehicleBridge.Flips} target 0" );
		Log.Info( $"[vp] AUDIT fallthroughs offenders={VehicleBridge.FallThroughs} target 0" );
		Log.Info( $"[vp] AUDIT stuck offenders={VehicleBridge.StuckTicks} target 0" );
		Log.Info( $"[vp] AUDIT nan_forces offenders={VehicleBridge.NanForces} target 0" );
		Log.Info( $"[vp] AUDIT sleep_while_driving offenders={VehicleBridge.SleepWhileDriving} target 0" );
	}

	static bool IsNan( Vector3 v ) => float.IsNaN( v.x ) || float.IsNaN( v.y ) || float.IsNaN( v.z );

	static bool IsBodyAsleep( Rigidbody rb )
	{
		var body = rb?.PhysicsBody;
		return body is not null && body.Sleeping;
	}
}
