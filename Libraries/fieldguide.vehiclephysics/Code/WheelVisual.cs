namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Spins and vertically tracks the wheel's visual mesh from VehicleWheel state.
/// Steering yaw lives on the wheel GameObject itself (set by VehicleController);
/// spin composes with whatever base orientation the factory gave the visual.
/// </summary>
public sealed class WheelVisual : Component
{
	public VehicleWheel Wheel { get; set; }

	Rotation _baseRotation;
	float _spinDegrees;

	protected override void OnStart()
	{
		_baseRotation = LocalRotation;
	}

	protected override void OnUpdate()
	{
		if ( Wheel is null )
			return;

		_spinDegrees += Wheel.AngularVelocity.RadianToDegree() * Time.Delta;
		_spinDegrees %= 360f;

		// Spin about the model-local +Y axle. FromPitch uses the Source pitch sign (positive pitch
		// tilts the local +X forward vector DOWN), so a POSITIVE angle here rolls the top of the wheel
		// toward the car's +X travel direction, the correct rolling sense for forward motion. The old
		// negated angle rolled the tread backwards while driving forward (community report: "wheels
		// rotate the wrong way"). AngularVelocity is +forward, so the pitch angle is used as-is.
		// Per-frame is correct for SPIN: the accumulator integrates with frame dt, and rotation
		// interpolates in its own buffer independent of the fixed-tick position write below.
		LocalRotation = _baseRotation * Rotation.FromPitch( _spinDegrees );
	}

	/// <summary>
	/// Suspension tracking runs at the FIXED tick, not per frame (ramp-hitch fix, 2026-07-21,
	/// LIVE-UNVERIFIED). <see cref="VehicleWheel.SuspensionLength"/> is a raw physics-tick field:
	/// writing it to LocalPosition per RENDER frame is the documented sawtooth anti-pattern (KB
	/// g-game-camera-follows-raw-fixedtick-feet-model-sawtooths): the body renders engine-interpolated
	/// while the wheels step at 50 Hz, so wheels judder against the body exactly where suspension
	/// length changes fast (ramp faces, transients), worse with speed and refresh rate; a wheel that
	/// unloads for one tick snapped 5-10 cm to full droop and back inside 1-2 frames. Owner
	/// discriminator: fps_max 50 (render rate = tick rate) made the felt ramp hitch "a million times
	/// better". Writing inside OnFixedUpdate lets GameTransform interpolation carry the motion per
	/// frame, exactly like the chassis (KB g-game-manual-visual-smoother-fights-fixedupdate-
	/// interpolation: never hand-smooth what engine interpolation already covers).
	/// </summary>
	protected override void OnFixedUpdate()
	{
		if ( Wheel is null )
			return;

		LocalPosition = Vector3.Down * Wheel.SuspensionLength * Units.MetersToUnits;
	}
}
