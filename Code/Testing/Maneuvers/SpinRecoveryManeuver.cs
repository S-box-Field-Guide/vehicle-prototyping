namespace VehicleProto;

/// <summary>
/// Spin-recovery probe (feel session 2026-07-15). Measures the exact complaint the
/// <see cref="VehicleController.ApplySpinRecoveryAssist"/> channel targets: after a handbrake flick
/// spins the car ~180°, the player holds full FORWARD throttle but the car keeps rolling BACKWARDS (its
/// old travel direction) before the tires pick up grip and drive it the new way. Lower recoveryS +
/// rollbackM = the stale velocity dies sooner.
///
/// Profile (scripted InputOverride — a repeatable flick, not a live human one):
///   0. ACCELERATE straight to entrySpeedMs (throttle 1, steer 0).
///   1. FLICK — handbrake + full steer + light maintenance throttle, held until cumulative yaw reaches
///      spinTargetDeg (~160°) so the car faces roughly opposite its motion. Yaw is re-baselined at flick
///      start; a rotation-time safety ends the phase if the car bogs so a run never hangs the phase.
///   2. RECOVER — release the handbrake, hold FULL forward throttle, steer 0. The clock from this
///      instant to forwardSpeed &gt; +0.5 m/s is <c>recoveryS</c>; <c>rollbackM</c> is the furthest travel
///      in the old (pre-recovery) travel direction after release. Both update live so a maxRunS DNF
///      reports the full un-recovered duration rather than a false 0 (same idiom as driftexit). Ends
///      when recovered.
///
/// A spec may pin this run's <see cref="CarDefinition.SpinRecoveryAssist"/> via the
/// <c>spinAssistMs2</c> param (0 = assist off) for a clean before/after A/B on JUST this channel — the
/// spawned def is a fresh instance (CarDefinitions.* =&gt; new()), so the override never leaks across runs.
///
/// Metrics (frozen contract, docs/testing-harness.md §6.2): <c>recoveryS</c>, <c>rollbackM</c>.
/// Never applies forces — only stages <see cref="DriveInputs"/>; determinism law: no RNG.
/// </summary>
public sealed class SpinRecoveryManeuver : ManeuverBase
{
	public override string Name => "spinrecovery";

	int _phase;
	float _releaseTime;
	float _phaseStartTime;
	Vector3 _releasePosM;
	Vector3 _oldTravelDir;
	bool _recovered;

	public override void Start( ManeuverContext ctx )
	{
		_phase = 0;
		_releaseTime = 0f;
		_phaseStartTime = 0f;
		_recovered = false;
		VehicleBridge.RollbackM = 0f;

		// A/B override for the before/after measurement: a spec may pin this run's dial (0 = assist off).
		// Param() returns the car's existing default when the key is absent, so this is a no-op then. The
		// spawned def is a fresh instance, so mutating it here does not leak into any other run.
		if ( ctx.Car?.Definition != null )
			ctx.Car.Definition.SpinRecoveryAssist =
				ctx.Param( "spinAssistMs2", ctx.Car.Definition.SpinRecoveryAssist );
	}

	static float ForwardSpeedMs( ManeuverContext ctx )
		=> Vector3.Dot( ctx.Body.Velocity * Units.UnitsToMeters, ctx.Car.WorldRotation.Forward );

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float entry = ctx.Param( "entrySpeedMs", 15f );        // ~54 km/h
		float flickSteer = ctx.Param( "flickSteer", 1f );      // full lock into the flick
		float flickThrottle = ctx.Param( "flickThrottle", 0.3f );
		float spinTarget = ctx.Param( "spinTargetDeg", 160f ); // yaw at which we release (roughly reversed)
		float rotSafetyS = ctx.Param( "rotSafetyS", 4f );      // flick phase safety timeout

		// phase 0: build to entry speed dead straight
		if ( _phase == 0 )
		{
			ctx.Drive( 1f, 0f, false );
			if ( VehicleBridge.SpeedMs >= entry )
			{
				_phase = 1;
				_phaseStartTime = ctx.RunTime;
				ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: true );
			}
			return false;
		}

		// phase 1: handbrake flick, held until the car has rotated ~spinTarget degrees (or bogs down)
		if ( _phase == 1 )
		{
			ctx.Drive( flickThrottle, flickSteer, true );
			bool spun = MathF.Abs( ctx.Telemetry.YawAccumDeg ) >= spinTarget;
			bool timedOut = ctx.RunTime - _phaseStartTime > rotSafetyS;
			if ( spun || timedOut )
			{
				_phase = 2;
				_releaseTime = ctx.RunTime;
				_releasePosM = ctx.Car.WorldPosition * Units.UnitsToMeters;
				// old travel direction = where the car is still sliding at release (its momentum).
				var flatVel = (ctx.Body.Velocity * Units.UnitsToMeters).WithZ( 0f );
				_oldTravelDir = flatVel.IsNearZeroLength
					? ctx.Car.WorldRotation.Backward.WithZ( 0f ).Normal
					: flatVel.Normal;
			}
			return false;
		}

		// phase 2: release + full forward throttle out; measure the recovery.
		ctx.Drive( 1f, 0f, false );

		// rollback = furthest travel in the old (pre-recovery) travel direction since release.
		var rel = (ctx.Car.WorldPosition * Units.UnitsToMeters - _releasePosM).WithZ( 0f );
		VehicleBridge.RollbackM = MathF.Max( VehicleBridge.RollbackM, Vector3.Dot( rel, _oldTravelDir ) );

		if ( !_recovered )
		{
			VehicleBridge.RecoveryS = ctx.RunTime - _releaseTime;
			if ( ForwardSpeedMs( ctx ) > 0.5f )
			{
				_recovered = true;
				return true;
			}
		}
		return false;
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"recoveryS={VehicleBridge.RecoveryS:F2}", "Recovery time", $"{VehicleBridge.RecoveryS:F2} s" );
		v.Add( $"rollbackM={VehicleBridge.RollbackM:F2}", "Rollback", $"{VehicleBridge.RollbackM:F2} m" );
		v.YawSummary = _recovered ? "recovered" : "stuck";
	}
}
