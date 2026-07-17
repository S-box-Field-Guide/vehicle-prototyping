namespace VehicleProto;

/// <summary>
/// Hill-grade climb (docs/handling-targets.md "Hill grade" rows). The grade ladder is a PARALLEL FAN —
/// one grade per row, every ramp base at the same X (<see cref="TestTrack.HillLadder"/> is the shared
/// layout truth) — so the maneuver routes on flat ground to its rated grade's row, aligns, and climbs
/// that ramp directly. (The first live battery on the old SERIAL ladder measured an obstacle course —
/// cars jumped off every lower ramp's elevated east edge en route — not grade-holding; layout redesigned
/// 2026-07-13.)
///
/// Phases (all one pure-pursuit rule — chase a look-ahead point on the rated row's centreline; the
/// pursuit converges onto the row then straightens east):
///   TRANSIT — cruise toward the row at a transit speed, dropping to <c>entrySpeedMs</c> when aligned
///   near the base; CLIMB — full throttle from the base; <c>climbed</c> latches when forward progress
///   reaches ~75% up the rated ramp's planar length (the run ends there, still on the slope — never
///   over the crest's drop edge). Rollback/stall tracking arms only in the climb phase so transit
///   cornering can't contaminate <c>rollbackM</c>.
///
/// Reports <c>climbed</c>, <c>rollbackM</c> (max backward slide from the furthest-up point while
/// climbing), and <c>wheelspinS</c> (accumulator). Only stages inputs + reads telemetry; computes no
/// physics. Ends on success or a 3 s stall, else maxRunS. Determinism law: no RNG.
/// </summary>
public sealed class HillClimbManeuver : ManeuverBase
{
	public override string Name => "hillclimb";

	float _maxForwardM;
	float _stallTimer;
	bool _climbing;

	public override void Start( ManeuverContext ctx )
	{
		_maxForwardM = 0f;
		_stallTimer = 0f;
		_climbing = false;
		VehicleBridge.Climbed = false;
		VehicleBridge.RollbackM = 0f;
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float entry = ctx.Param( "entrySpeedMs", 10f );
		float ratedGrade = ctx.Param( "gradePct", 20f );

		// rated grade -> the built row at-or-below it (discrete-ladder approximation of "holds N%").
		ResolveRow( ratedGrade, out float rowY, out float rowGrade );

		var carPosM = ctx.Car.WorldPosition * Units.UnitsToMeters;
		var rel = (carPosM - ctx.Telemetry.StartPosM).WithZ( 0 );
		float fwd = Vector3.Dot( rel, ctx.Telemetry.StartFwd );   // planar +X progress from spawn (m)
		float lat = Vector3.Dot( rel, ctx.Telemetry.StartLeft );  // lateral offset from spawn row (m; +Y)

		float latTarget = rowY - TestTrack.HillLaneYMeters;       // rated row, in the spawn frame
		float baseProgress = TestTrack.HillBaseXMeters - TestTrack.HillLadderSpawnXMeters;
		float pitchRad = MathF.Atan( rowGrade / 100f );
		float crestProgress = baseProgress + TestTrack.HillRampLength * MathF.Cos( pitchRad ) * 0.75f;

		// one pursuit rule for the whole run: chase a look-ahead point on the rated row's centreline.
		const float lookaheadM = 9f;
		var targetPt = ctx.Telemetry.StartPosM + ctx.Telemetry.StartFwd * (fwd + lookaheadM)
			+ ctx.Telemetry.StartLeft * latTarget;
		var toTarget = (targetPt - carPosM).WithZ( 0 );
		var carFwd = ctx.Car.WorldRotation.Forward.WithZ( 0 ).Normal;
		float cross = carFwd.x * toTarget.y - carFwd.y * toTarget.x; // +z = target to the LEFT (CCW)
		float dot = carFwd.x * toTarget.x + carFwd.y * toTarget.y;
		float steer = Math.Clamp( MathF.Atan2( cross, dot ) * 2.0f, -1f, 1f );

		if ( !_climbing && fwd >= baseProgress - 2f )
			_climbing = true;

		if ( !_climbing )
		{
			// TRANSIT: faster than entry speed while far off the row (long south legs must fit maxRunS),
			// entry speed once aligned near the base. Bang-bang cruise, same idiom as CrashManeuver.
			bool aligned = MathF.Abs( lat - latTarget ) < 4f;
			float cruise = aligned ? entry : MathF.Max( entry, 12f );
			float speed = VehicleBridge.SpeedMs;
			float drive = speed < cruise ? 1f : (speed > cruise + 1.5f ? 0f : 0.2f);
			ctx.Drive( drive, steer, false );
			return false;
		}

		// CLIMB: full throttle up the rated ramp; rollback = max backward slide from the furthest-up
		// point (armed only in this phase so transit cornering can't contaminate it).
		ctx.Drive( 1f, steer, false );
		_maxForwardM = MathF.Max( _maxForwardM, fwd );
		VehicleBridge.RollbackM = MathF.Max( VehicleBridge.RollbackM, _maxForwardM - fwd );

		if ( _maxForwardM >= crestProgress )
		{
			VehicleBridge.Climbed = true;
			return true; // stop on the slope, before the crest's drop edge
		}

		// sustained stall (full throttle, no motion) = the car can't hold this grade; don't idle to maxRunS.
		_stallTimer = VehicleBridge.SpeedMs < 0.5f ? _stallTimer + dt : 0f;
		return _stallTimer > 3f;
	}

	/// <summary>The built row whose grade is the largest at-or-below <paramref name="ratedGrade"/>.</summary>
	static void ResolveRow( float ratedGrade, out float rowY, out float rowGrade )
	{
		rowY = TestTrack.HillLaneYMeters;
		rowGrade = 0f;
		foreach ( var (y, g) in TestTrack.HillLadder )
			if ( g <= ratedGrade + 0.01f && g > rowGrade ) { rowGrade = g; rowY = y; }
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"climbed={VehicleBridge.Climbed}", "Climbed", VehicleBridge.Climbed ? "yes" : "no" );
		v.Add( $"rollbackM={VehicleBridge.RollbackM:F2}", "Rollback", $"{VehicleBridge.RollbackM:F2} m" );
		v.Add( $"wheelspinS={VehicleBridge.WheelspinS:F2}", "Wheelspin", $"{VehicleBridge.WheelspinS:F2} s" );
	}
}
