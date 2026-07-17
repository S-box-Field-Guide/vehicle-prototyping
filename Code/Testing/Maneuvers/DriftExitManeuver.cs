namespace VehicleProto;

/// <summary>
/// Drift-exit recovery probe. Addresses a common kart complaint: drift ENTRY feels good, but EXIT
/// is "stuck sliding sideways, lose too much
/// momentum". This maneuver measures that exit numerically so a dial change can be gated on it.
///
/// Profile (RWD complaint cars — kart, coupe):
///   0. ACCELERATE straight to entrySpeedMs (throttle 1, steer 0).
///   1. SLIDE — handbrake-initiate at a fixed steer + light maintenance throttle, held until the
///      cumulative yaw offset reaches yawTargetDeg (~120°). Yaw is re-baselined at slide start.
///   2. EXIT — release the handbrake, ease the steering back, full throttle out. The clock from this
///      instant to |rear slip angle| &lt; recoverSlipDeg is <c>exitRecoveryS</c>; the run ends there.
///
/// Metrics (frozen contract, docs/testing-harness.md §6.2):
///   <c>exitRecoveryS</c>  — hb-release → rear slip settles (lower = catches sooner). Reads the FULL
///                            phase-2 duration on a maxRunS DNF so an un-recovered slide fails the band
///                            rather than reporting a false 0.
///   <c>speedRetention</c> — exitSpeed/entrySpeed at recovery (higher = less momentum scrubbed).
///   <c>peakSlipDeg</c>    — deepest |rear slip angle| across the whole slide (diagnostic).
///
/// Never applies forces — only stages <see cref="DriveInputs"/>; determinism law: no RNG.
/// </summary>
public sealed class DriftExitManeuver : ManeuverBase
{
	public override string Name => "driftexit";

	int _phase;
	float _releaseTime;
	float _entrySpeed;
	bool _recovered;

	public override void Start( ManeuverContext ctx )
	{
		_phase = 0;
		_releaseTime = 0f;
		_entrySpeed = 0f;
		_recovered = false;
	}

	/// <summary>Average |slip angle| (deg) over the grounded rear wheels — the tele line's rearA.</summary>
	static float RearSlipDeg( ManeuverContext ctx )
		=> ctx.Car.Wheels.Where( w => !w.IsSteering && w.IsGrounded )
			.Select( w => MathF.Abs( w.SlipAngle.RadianToDegree() ) )
			.DefaultIfEmpty( 0f ).Average();

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float entry = ctx.Param( "entrySpeedMs", 22f );
		float steer = ctx.Param( "driftSteer", 0.7f );      // fixed steer into the slide (+ = left)
		float yawTarget = ctx.Param( "yawTargetDeg", 120f ); // release the handbrake at this yaw offset
		float recoverDeg = ctx.Param( "recoverSlipDeg", 8f );

		// phase 0: build to entry speed dead straight
		if ( _phase == 0 )
		{
			ctx.Drive( 1f, 0f, false );
			if ( VehicleBridge.SpeedMs >= entry )
			{
				_phase = 1;
				_entrySpeed = VehicleBridge.SpeedMs;
				ctx.Telemetry.ResetYawTracking( ctx.Car, resetMax: true );
			}
			return false;
		}

		// phase 1: handbrake slide at a fixed steer, held to the yaw target
		if ( _phase == 1 )
		{
			ctx.Drive( 0.3f, steer, true );
			VehicleBridge.PeakSlipDeg = MathF.Max( VehicleBridge.PeakSlipDeg, RearSlipDeg( ctx ) );
			if ( MathF.Abs( ctx.Telemetry.YawAccumDeg ) >= yawTarget )
			{
				_phase = 2;
				_releaseTime = ctx.RunTime;
			}
			return false;
		}

		// phase 2: release + power out; measure the recovery. exitRecoveryS/speedRetention update live
		// (so a maxRunS DNF reports the full un-recovered duration, not a false 0) and latch at recovery.
		ctx.Drive( 1f, steer * 0.3f, false );
		float rearDeg = RearSlipDeg( ctx );
		VehicleBridge.PeakSlipDeg = MathF.Max( VehicleBridge.PeakSlipDeg, rearDeg );
		if ( !_recovered )
		{
			VehicleBridge.ExitRecoveryS = ctx.RunTime - _releaseTime;
			VehicleBridge.SpeedRetention = _entrySpeed > 0.1f ? VehicleBridge.SpeedMs / _entrySpeed : 0f;
			if ( rearDeg < recoverDeg )
			{
				_recovered = true;
				return true;
			}
		}
		return false;
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"exitRecoveryS={VehicleBridge.ExitRecoveryS:F2}", "Exit recovery", $"{VehicleBridge.ExitRecoveryS:F2} s" );
		v.Add( $"speedRetention={VehicleBridge.SpeedRetention:F2}", "Speed retention", $"{VehicleBridge.SpeedRetention:F2}" );
		v.Add( $"peakSlipDeg={VehicleBridge.PeakSlipDeg:F1}", "Peak rear slip", $"{VehicleBridge.PeakSlipDeg:F1}°" );
		v.YawSummary = _recovered ? "recovered" : "stuck";
	}
}
