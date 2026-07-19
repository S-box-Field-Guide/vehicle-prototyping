namespace VehicleProto;

/// <summary>Hold the authored <c>approachSpeedMs</c> up to the ramp (bang-bang cruise, the same hold
/// pattern <see cref="WashboardManeuver"/> uses), then COMMIT full throttle through the ramp face and
/// over the lip so ramp-entry speed is set by the parameter — not by whatever top speed the car's
/// gearing happens to reach on the run-up. The accumulator captures airtime / landing attitude /
/// settle (all gated on the real ramp event, not the spawn settle). Ends 2 s after a real landing.
///
/// Why the two phases (audit 2026-07-13 MEDIUM — the old profile ignored <c>approachSpeedMs</c> and
/// always floored it): the cruise makes 18 vs 22 m/s produce measurably different ramp-entry speeds;
/// the latched full-throttle commit keeps power ON across take-off so there is no throttle-lift pitch
/// artifact at the lip (a lift-off would pitch the nose and corrupt landing attitude).
///
/// Optional <c>airLift</c> param (default 0, off) cuts throttle once airborne, for an A/B isolating
/// whether in-air drive torque tumbles light cars off jumps.</summary>
public sealed class JumpManeuver : ManeuverBase
{
	public override string Name => "jump";

	// forward metres from spawn at which we floor it no-matter-what (late backstop right at the first
	// ramp base ~30 m ahead of the "ramps" spawn; the pitch/airborne triggers below normally fire first).
	const float CommitFwdM = 29f;
	// nose-pitch magnitude (deg) that means the car is climbing the ramp face — commit here so the
	// approach hold keeps authority right up to the ramp (accel squat is <1°, so 4° cleanly = ramp).
	const float RampPitchDeg = 4f;

	bool _committed;

	public override void Start( ManeuverContext ctx ) => _committed = false;

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float approach = ctx.Param( "approachSpeedMs", 22f );

		// forward progress from spawn along the launch lane (planar +X of the start frame)
		var carPosM = ctx.Car.WorldPosition * Units.UnitsToMeters;
		float fwd = Vector3.Dot( (carPosM - ctx.Telemetry.StartPosM).WithZ( 0 ), ctx.Telemetry.StartFwd );
		float pitch = MathF.Abs( ctx.Car.WorldRotation.Angles().pitch );

		// commit latches once the car is ON the ramp face (pitching up) or has left the ground, or as a
		// late backstop at the ramp base — and never releases. From here it is FULL throttle through the
		// ramp and over the lip (keep throttle = no lift-off pitch artifact). Latching on the pitch/air
		// event keeps the approach-speed hold in authority right up to the ramp so ramp-ENTRY speed tracks
		// approachSpeedMs (a fixed early commit would re-accelerate before the lip and wash the cap out).
		if ( pitch > RampPitchDeg || ctx.Telemetry.ContactlessS > 0f || fwd > CommitFwdM )
			_committed = true;

		float drive;
		if ( _committed )
		{
			// airLift=1: cut throttle while airborne (a human lifts in the air; the default keeps
			// power ON so ramp-entry speed stays parameter-controlled). The A/B isolates whether
			// in-air drive torque, spinning the unloaded wheels up and pitching the chassis in
			// reaction, is what tumbles light cars off jumps.
			bool airborne = ctx.Telemetry.ContactlessS > 0.05f;
			drive = (ctx.Param( "airLift", 0f ) > 0.5f && airborne) ? 0f : 1f;
		}
		else
		{
			// bang-bang cruise to the approach speed (WashboardManeuver pattern): below target -> throttle,
			// well above -> coast, in-band -> light maintain. Where the cap sits below the speed the car can
			// reach in the run-up it BINDS and sets ramp-entry speed; a cap above the achievable run-up speed
			// simply never engages (full throttle) — see docs/baseline-metrics.md jump A/B for the measured
			// binding range on the ramps station.
			float speed = VehicleBridge.SpeedMs;
			drive = speed < approach ? 1f : (speed > approach + 1.5f ? 0f : 0.2f);
		}
		ctx.Drive( drive, 0f, false );

		if ( ctx.Telemetry.Landed && ctx.RunTime - ctx.Telemetry.LandTime > 2f )
			return true; // landed and had time to settle
		return false;
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"airtimeS={VehicleBridge.AirtimeS:F2}", "Airtime", $"{VehicleBridge.AirtimeS:F2} s" );
		v.Add( $"landingPitchDeg={VehicleBridge.LandingPitchDeg:F1}", "Landing pitch", $"{VehicleBridge.LandingPitchDeg:F1}°" );
		v.Add( $"settleS={VehicleBridge.SettleS:F2}", "Settle", $"{VehicleBridge.SettleS:F2} s" );
	}
}
