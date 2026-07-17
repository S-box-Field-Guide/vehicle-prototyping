using System.Globalization;

namespace VehicleProto;

/// <summary>
/// Formatting helpers for values interpolated into razor inline <c>style=</c> attributes.
/// <para>
/// WHY THIS EXISTS: the s&amp;box engine styler parses an inline style like
/// <c>style="height: @(x)%"</c> as a mini-stylesheet, and a raw <c>float.ToString()</c> emits
/// SCIENTIFIC NOTATION for denormal/near-zero values (e.g. a throttle residual of <c>1.67e-12</c>
/// renders as <c>"1.6734703E-10%"</c>). The styler rejects that with a Code Error —
/// <c>"1.6734703E-10% is not valid with height [:0]"</c> — and drops the declaration. A comma-decimal
/// locale would break the parser the same way. Both are avoided by formatting fixed-point and
/// culture-invariant, and clamping to a sane range so degenerate physics can't emit garbage.
/// </para>
/// Use inline as: <c>style="height: @UiFmt.Pct( frac * 100f )%"</c>.
/// </summary>
public static class UiFmt
{
	/// <summary>A CSS-percentage NUMBER (the value BEFORE the <c>%</c> sign), clamped to
	/// <c>[0, 100]</c> and formatted fixed-point / invariant so it can never be scientific notation
	/// nor a comma decimal. F3 keeps bar/marker motion smooth without ever going exponential.</summary>
	public static string Pct( float value )
		=> System.Math.Clamp( value, 0f, 100f ).ToString( "F3", CultureInfo.InvariantCulture );

	/// <summary>Same as <see cref="Pct(float)"/> but with a caller-chosen clamp window, for values
	/// that legitimately span a different range before the <c>%</c> (still never scientific).</summary>
	public static string Pct( float value, float min, float max )
		=> System.Math.Clamp( value, min, max ).ToString( "F3", CultureInfo.InvariantCulture );
}
