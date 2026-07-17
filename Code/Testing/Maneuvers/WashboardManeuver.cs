namespace VehicleProto;

/// <summary>
/// Rough-ground (washboard) traverse (docs/handling-targets.md "Washboard contact-loss" rows). Drive
/// the transverse-ridge section at a target speed and let the accumulator read out the fields the
/// spec asserts: <c>wheelContactLossPct</c> (average % of PER-WHEEL contact lost — the metric the
/// bands were authored against, per handling-targets feel-heuristic 3's "per-wheel IsGrounded"
/// provenance; the full-airborne <c>contactLossPct</c> read 0 on every car live because raycast
/// wheels skipping 0.12 m ridges rarely put the WHOLE car airborne — finding 2026-07-13) and
/// <c>settleS</c> (the no-resonance proxy; stays 0 unless a ridge launches the car fully airborne
/// past the accumulator's arming window).
///
/// The maneuver only stages inputs — bang-bang cruise at <c>approachSpeedMs</c> plus a light
/// centreline lane-keep so it stays on the 12 m-wide ridge lane — and reads telemetry; it computes no
/// physics. Ends once well past the ~30 m ridge field. Determinism law: no RNG.
///
/// NOTE (measurement window): <c>wheelContactLossPct</c> is accumulated over the WHOLE run (approach +
/// ridges + a little exit), so the flat approach dilutes it below a "% of ridge-transit-time only"
/// reading. The station spawn (15 m before the ridges) is authored, not chosen here; the dilution is a
/// documented approximation, not a tuned dial (docs/testing-harness.md §6.2).
/// </summary>
public sealed class WashboardManeuver : ManeuverBase
{
	public override string Name => "washboard";

	public override void Start( ManeuverContext ctx ) { }

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		float target = ctx.Param( "approachSpeedMs", 15f );

		var carPosM = ctx.Car.WorldPosition * Units.UnitsToMeters;
		var rel = (carPosM - ctx.Telemetry.StartPosM).WithZ( 0 );
		float fwd = Vector3.Dot( rel, ctx.Telemetry.StartFwd );   // forward progress from spawn (m)
		float lat = Vector3.Dot( rel, ctx.Telemetry.StartLeft );  // lateral drift off the lane centre

		// hold the target cruise across the ridges (a fixed full-throttle run would keep accelerating,
		// changing the ridge-strike frequency between cars); gentle lane-keep on the centreline.
		float speed = VehicleBridge.SpeedMs;
		float drive = speed < target ? 1f : (speed > target + 1.5f ? 0f : 0.2f);
		float steer = Math.Clamp( -lat * 0.15f, -0.4f, 0.4f );
		ctx.Drive( drive, steer, false );

		// spawn is ~15 m before the ridge field, ridges span ~30 m -> 60 m clears them with margin.
		return fwd > 60f;
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"wheelContactLossPct={VehicleBridge.WheelContactLossPct:F1}", "Wheel contact loss", $"{VehicleBridge.WheelContactLossPct:F1}%" );
		v.Add( $"settleS={VehicleBridge.SettleS:F2}", "Settle", $"{VehicleBridge.SettleS:F2} s" );
	}
}
