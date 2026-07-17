namespace VehicleProto;

/// <summary>
/// Parametric peaked tire curve (Pacejka-shaped, authored by peak/tail points per spec §5.2.1).
/// Rises smoothly to a grip peak, then falls off to a sliding asymptote.
/// Input is |slip| — slip ratio (unitless) for longitudinal, slip angle (radians) for lateral.
/// Output is a grip coefficient multiplied by tire load to get force.
/// </summary>
public struct TireCurve
{
	/// <summary>Slip value where grip peaks (κ ≈ 0.08–0.12, α ≈ 6–9° in radians).</summary>
	public float PeakSlip { get; set; }

	/// <summary>Grip coefficient at the peak.</summary>
	public float PeakGrip { get; set; }

	/// <summary>Slip value where the curve has fully fallen to the tail.</summary>
	public float TailSlip { get; set; }

	/// <summary>Grip coefficient when fully sliding (typically 75–85% of peak).</summary>
	public float TailGrip { get; set; }

	public TireCurve( float peakSlip, float peakGrip, float tailSlip, float tailGrip )
	{
		PeakSlip = peakSlip;
		PeakGrip = peakGrip;
		TailSlip = tailSlip;
		TailGrip = tailGrip;
	}

	public readonly float Evaluate( float slip )
	{
		slip = MathF.Abs( slip );

		if ( slip <= PeakSlip )
		{
			// parabolic rise with zero slope at the peak
			float n = slip / PeakSlip;
			return PeakGrip * n * (2f - n);
		}

		// smoothstep decay from peak down to the tail
		float t = Math.Clamp( (slip - PeakSlip) / (TailSlip - PeakSlip), 0f, 1f );
		t = t * t * (3f - 2f * t);
		return PeakGrip + (TailGrip - PeakGrip) * t;
	}

	public static TireCurve Street => new( 0.10f, 1.00f, 0.45f, 0.80f );
	public static TireCurve Sport => new( 0.09f, 1.15f, 0.40f, 0.92f );
	public static TireCurve Offroad => new( 0.14f, 0.90f, 0.60f, 0.75f );
}
