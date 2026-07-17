namespace VehicleProto;

/// <summary>Position-locked pure-pursuit weave through alternating cones (NOT the old open-loop
/// sin(time) steer that drifted out of phase and plowed a cone). Tracks forward progress from the
/// spawn frame and steers a look-ahead line that passes each cone on its open side; completes a full
/// gate past the last cone. Counts cone strikes by proximity to live "Slalom Cone" objects.</summary>
public sealed class SlalomManeuver : ManeuverBase
{
	public override string Name => "slalom";

	bool _coneHitThisTick;

	public override void Start( ManeuverContext ctx ) => _coneHitThisTick = false;

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		_coneHitThisTick = false; // per-tick edge for cone-proximity detection

		float spacing = ctx.Param( "coneSpacingM", 18f );
		float coneCount = ctx.Param( "coneCount", 8f );
		// Cones sit only ±2.5 m off a clear centerline, so a SMALL weave already passes each on its
		// open side; a big swing at gate spacing would demand >2 g lateral (far beyond ~0.7 g grip)
		// and the car would understeer wide into the cone. Keep amplitude/cruise inside tire grip.
		float amp = ctx.Param( "weaveAmpM", 1.0f );
		float lookahead = ctx.Param( "lookaheadM", 7f );
		float cruise = ctx.Param( "cruiseSpeedMs", 16f );   // ~58 km/h; a ±1.0 m weave is grip-followable here

		var carPosM = ctx.Car.WorldPosition * Units.UnitsToMeters;
		var rel = (carPosM - ctx.Telemetry.StartPosM).WithZ( 0 );
		float fwd = Vector3.Dot( rel, ctx.Telemetry.StartFwd );      // forward progress from spawn (m)

		// Cones sit at forward f = spacing*(i+1), alternating +lateral (even i) / -lateral (odd i) along
		// the start-frame LEFT axis. Target the side OPPOSITE the cone nearest the look-ahead point — a
		// square target the pure-pursuit smooths into a weave. Keyed to POSITION (not sin(time)).
		float fAhead = fwd + lookahead;
		int coneIdx = (int)MathF.Round( (fAhead - spacing) / spacing );
		coneIdx = Math.Clamp( coneIdx, 0, (int)coneCount - 1 );
		float targetLat = (coneIdx % 2 == 0) ? -amp : amp;   // opposite the cone's own side
		var targetPt = ctx.Telemetry.StartPosM + ctx.Telemetry.StartFwd * fAhead + ctx.Telemetry.StartLeft * targetLat;

		var toTarget = (targetPt - carPosM).WithZ( 0 );
		var carFwd = ctx.Car.WorldRotation.Forward.WithZ( 0 ).Normal;
		float cross = carFwd.x * toTarget.y - carFwd.y * toTarget.x; // +z = target to the LEFT (CCW)
		float dot = carFwd.x * toTarget.x + carFwd.y * toTarget.y;
		float angErr = MathF.Atan2( cross, dot );                    // rad; + = steer LEFT (+Steer)

		// Pilot hardening (2026-07-13): the fixed high-gain pursuit (angErr*2.0) drove the
		// light twitchy RWD kart at its stability boundary, so its cold-first-play sample diverged from
		// the warm attractor (18.5 s / 309 deg/s vs 16.1 s / 180). A yaw-rate backoff (drift-catch-style
		// damping, PILOT-ONLY — no CarDefinition dial touched, per the standing rule that the harness may
		// not mask car character) bleeds steer gain as |yawRate| climbs PAST a gate set ABOVE every stable
		// car's peak (hatch 36 / pickup 72 / coupe 149 deg/s at baseline), so hatch/coupe/pickup steer
		// BIT-IDENTICALLY (excess=0 -> gain=baseGain) while the kart's fishtail excursions are arrested
		// and its cold==warm outcome converges. The gate stays >150 so the kart still reads its twitchy
		// signature (not flattened below ~150). Defaults leave every existing spec row unchanged; all
		// three knobs are Param-tunable.
		float baseGain = ctx.Param( "steerGain", 2.0f );
		float yawGate = ctx.Param( "steerYawDampGateDeg", 155f );    // below this: no damping (stable cars)
		float yawDampRef = ctx.Param( "steerYawDampRefDeg", 160f );  // larger excess -> more gain backoff
		float yawNowDeg = ctx.Body.IsValid() ? MathF.Abs( ctx.Body.AngularVelocity.z.RadianToDegree() ) : 0f;
		float yawExcess = MathF.Max( 0f, yawNowDeg - yawGate );
		float gain = baseGain / (1f + yawExcess / MathF.Max( yawDampRef, 1e-3f ));
		float steer = Math.Clamp( angErr * gain, -1f, 1f );

		float throttle = VehicleBridge.SpeedMs < cruise ? 0.7f : 0.15f;
		ctx.Drive( throttle, steer, false );
		CountConeStrikes( ctx );

		// done once the car is a full gate past the last cone (cone N-1 is at (coneCount)*spacing).
		return fwd > (coneCount + 1f) * spacing;
	}

	void CountConeStrikes( ManeuverContext ctx )
	{
		// proximity to any live "Slalom Cone" GameObject (no TestTrack change needed). A car within
		// ~1.2 m of a cone centre this tick counts as a strike edge.
		if ( _coneHitThisTick ) return;
		float m = Units.MetersToUnits;
		float rM = 1.2f;
		var carPos = ctx.Car.WorldPosition;
		foreach ( var go in ctx.Car.Scene.GetAllObjects( true ) )
		{
			if ( go.Name is null || !go.Name.StartsWith( "Slalom Cone" ) ) continue;
			if ( (go.WorldPosition.WithZ( carPos.z ) - carPos).Length < rM * m )
			{
				VehicleBridge.ConeStrikes++;
				_coneHitThisTick = true;
				break;
			}
		}
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"coneStrikes={VehicleBridge.ConeStrikes}", "Cone strikes", $"{VehicleBridge.ConeStrikes}" );
		v.Add( $"elapsedS={VehicleBridge.ElapsedS:F1}", "Elapsed", $"{VehicleBridge.ElapsedS:F1} s" );
		v.Add( $"yawRatePeakDeg={VehicleBridge.YawRatePeakDeg:F1}", "Yaw rate peak", $"{VehicleBridge.YawRatePeakDeg:F1} deg/s" );
	}
}
