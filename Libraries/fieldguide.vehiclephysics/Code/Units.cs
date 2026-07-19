namespace FieldGuide.VehiclePhysics;

/// <summary>
/// All vehicle math is done in SI units (meters, kg, N); convert only at the engine boundary.
/// s&amp;box uses Source-style inch units.
/// </summary>
public static class Units
{
	public const float MetersToUnits = 39.37f;
	public const float UnitsToMeters = 1f / MetersToUnits;
}
