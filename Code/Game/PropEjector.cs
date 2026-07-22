using System;

namespace VehicleProto;

/// <summary>
/// Arcade cone ejector (owner order 2026-07-21). Telemetry (ramptrace2-223331, t=41.7..59) showed
/// a full-speed cone hit ending with the cone WEDGED under the car's nose: front axle load halved
/// (~1600 N vs 3102 static), front suspension drooped to 0.153 m, nose pitched up 1.15 deg, car
/// creeping 0.3 m/s under throttle and unable to drive off it, cone invisible under the bodywork.
/// The wedged contact slept, so it produced no collision events while chocking the car.
///
/// Fix is deliberately FUN over physics: any cone inside the car's danger volume (footprint plus a
/// margin, from just below the belly up to the roof, in CAR-LOCAL space so an airborne car over a
/// ground cone is exempt) gets FLUNG out sideways with some up and spin. A cone deep under the
/// body is first teleported to the nearer side so no wedge can survive even one tick. Balls are
/// exempt on purpose: they are big enough never to wedge, and pushing them around is the fun.
/// </summary>
public sealed class PropEjector : Component
{
	public VehicleController Target { get; set; }

	// Margins beyond the body box. Forward margin means bumper touches FLING rather than push:
	// the arcade cone behavior the owner asked for ("extra bouncy, not really like physics").
	const float MarginX = 0.4f;   // m beyond nose/tail
	const float MarginY = 0.3f;   // m beyond the flanks
	const float BellyBand = 1.0f; // m below origin still counted as "under the car"

	protected override void OnFixedUpdate()
	{
		if ( Target is null || !Target.IsValid() || Target.Definition is null )
			return;

		var car = Target.GameObject;
		var tx = car.WorldTransform;
		var body = Target.Definition.BodySize; // meters (x length, y width, z height)
		float m = Units.MetersToUnits;
		float halfLen = (body.x * 0.5f + MarginX) * m;
		float halfWid = (body.y * 0.5f + MarginY) * m;
		float roofZ = body.z * m;
		float bellyZ = -BellyBand * m;

		var carVel = Target.SpeedMs;

		foreach ( var rb in Scene.GetAllComponents<Rigidbody>() )
		{
			if ( !rb.IsValid() || !rb.GameObject.Tags.Has( "cone" ) )
				continue;

			var local = tx.PointToLocal( rb.WorldPosition );
			if ( MathF.Abs( local.x ) > halfLen || MathF.Abs( local.y ) > halfWid )
				continue;
			if ( local.z < bellyZ || local.z > roofZ )
				continue;

			// Inside the danger volume: fling it out the nearer flank, up, and forward a touch.
			float side = local.y >= 0f ? 1f : -1f;
			var right = tx.Rotation.Right; // +y in car local space

			// Deep under the body (below origin height and within the footprint proper): the wedge
			// case. Teleport to the flank first so not even one tick of chock survives.
			bool wedged = local.z < 0f && MathF.Abs( local.x ) < body.x * 0.5f * m;
			if ( wedged )
				rb.WorldPosition = car.WorldPosition + right * side * (halfWid + 0.9f * m) + Vector3.Up * 0.5f * m;

			float fling = MathF.Max( 8f, carVel * 0.6f );
			fling = MathF.Min( fling, 24f );
			var vel = right * side * fling
				+ Vector3.Up * MathF.Min( 5f + carVel * 0.25f, 12f )
				+ tx.Rotation.Forward * carVel * 0.25f;
			rb.Velocity = vel * m;

			// deterministic tumble, seeded by where it was hit
			float spin = 6f + MathF.Abs( local.x * 0.01f ) % 4f;
			rb.AngularVelocity = new Vector3( side * spin, spin * 0.7f, side * 2f );
		}
	}
}
