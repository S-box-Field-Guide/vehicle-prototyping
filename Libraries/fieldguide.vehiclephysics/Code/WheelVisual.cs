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
		LocalRotation = _baseRotation * Rotation.FromPitch( _spinDegrees );
		LocalPosition = Vector3.Down * Wheel.SuspensionLength * Units.MetersToUnits;
	}
}
