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

		LocalRotation = _baseRotation * Rotation.FromPitch( -_spinDegrees );
		LocalPosition = Vector3.Down * Wheel.SuspensionLength * Units.MetersToUnits;
	}
}
