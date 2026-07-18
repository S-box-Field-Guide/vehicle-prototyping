namespace VehicleProto;

/// <summary>
/// TEMPORARY diagnostic maneuver for the kart high-PeakTorque yaw-wobble hunt (2026-07-18).
/// Straight-line full throttle from standstill for probeDurationS, recording per-tick yaw rate and
/// per-rear-wheel slip/load/omega, then emitting a summary + a 10 Hz downsampled trace as
/// greppable "[vp] wobbleprobe" / "[vp] wobbletrace" console lines. Params:
///   peakTorque      override CarDefinition.PeakTorque for this run (same seam the tuning panel writes)
///   symmetrize      1 = re-center the kit engine part collider laterally (model-frame X -> 0) so the
///                   derived inertia tensor loses its lateral products; A/B lever for the
///                   engine-collider-asymmetry pathway. Mass + mass-center overrides are untouched.
///   probeDurationS  run length (default 10 s)
/// NOT part of the battery: no spec file, no frozen-contract metrics. REMOVE when the hunt lands
/// (house temp-probe pattern).
/// </summary>
public sealed class WobbleProbeManeuver : ManeuverBase
{
	public override string Name => "wobbleprobe";

	float _dur;
	readonly List<float> _t = new();
	readonly List<float> _yaw = new();   // deg/s
	readonly List<float> _slipL = new(); // RL SlipRatio
	readonly List<float> _slipR = new(); // RR SlipRatio
	readonly List<float> _loadL = new();
	readonly List<float> _loadR = new();
	readonly List<float> _omL = new();
	readonly List<float> _omR = new();
	string _summary = "";

	public override void Start( ManeuverContext ctx )
	{
		_t.Clear(); _yaw.Clear(); _slipL.Clear(); _slipR.Clear();
		_loadL.Clear(); _loadR.Clear(); _omL.Clear(); _omR.Clear();
		_summary = "";
		_dur = ctx.Param( "probeDurationS", 10f );

		float pk = ctx.Param( "peakTorque", ctx.Car.Definition.PeakTorque );
		ctx.Car.Definition.PeakTorque = pk;

		bool symmetrize = ctx.Param( "symmetrize", 0f ) >= 0.5f;
		if ( symmetrize )
			SymmetrizeEngineCollider( ctx.Car );

		Log.Info( $"[vp] wobbleprobe start pk={pk:F0} sym={(symmetrize ? 1 : 0)} dur={_dur:F0}" );
	}

	/// <summary>Re-center the kit engine part collider laterally. The kart_kit engine part is the
	/// only laterally offset body collider (model-frame X = +0.34 m right); zeroing its X removes the
	/// lateral inertia products while mass and mass-center stay pinned by the rigidbody overrides.
	/// Purely a diagnostic A/B lever; the visual moves with it, which is fine for a probe run.</summary>
	static void SymmetrizeEngineCollider( VehicleController car )
	{
		foreach ( var child in car.GameObject.Children )
		{
			if ( child.Name != "Kit Body" )
				continue;
			foreach ( var part in child.Children )
			{
				if ( part.Name != "Part engine" )
					continue;
				var lp = part.LocalPosition;
				part.LocalPosition = lp.WithX( 0f );
				Log.Info( $"[vp] wobbleprobe symmetrized engine collider x {lp.x:F1} -> 0" );
				return;
			}
		}
		Log.Warning( "[vp] wobbleprobe: engine part not found (blockout kart?), no symmetrize applied" );
	}

	public override bool Tick( ManeuverContext ctx, float dt )
	{
		ctx.Drive( 1f, 0f, false );

		var rears = ctx.Car.Wheels.Where( w => !w.IsSteering ).ToList();
		if ( rears.Count == 2 )
		{
			_t.Add( ctx.RunTime );
			_yaw.Add( ctx.Body.AngularVelocity.z.RadianToDegree() );
			_slipL.Add( rears[0].SlipRatio );
			_slipR.Add( rears[1].SlipRatio );
			_loadL.Add( rears[0].Load );
			_loadR.Add( rears[1].Load );
			_omL.Add( rears[0].AngularVelocity );
			_omR.Add( rears[1].AngularVelocity );
		}

		if ( ctx.RunTime < _dur )
			return false;

		EmitTrace();
		return true;
	}

	void EmitTrace()
	{
		// summary window: t >= 2 s (post-launch transient), matching the offline analysis window
		int n = _t.Count;
		double sumSq = 0; int cnt = 0; float peak = 0f;
		int flips = 0; float prevD = 0f; float maxAbsD = 0f;
		float maxSlip = 0f, maxOm = 0f; double loadDiffSum = 0;
		for ( int i = 0; i < n; i++ )
		{
			float d = _slipL[i] - _slipR[i];
			maxSlip = MathF.Max( maxSlip, MathF.Max( MathF.Abs( _slipL[i] ), MathF.Abs( _slipR[i] ) ) );
			maxOm = MathF.Max( maxOm, MathF.Max( MathF.Abs( _omL[i] ), MathF.Abs( _omR[i] ) ) );
			if ( _t[i] >= 2f )
			{
				sumSq += _yaw[i] * _yaw[i]; cnt++;
				peak = MathF.Max( peak, MathF.Abs( _yaw[i] ) );
				if ( prevD * d < 0f ) flips++;
				maxAbsD = MathF.Max( maxAbsD, MathF.Abs( d ) );
				loadDiffSum += _loadL[i] - _loadR[i];
				prevD = d;
			}
		}
		float rms = cnt > 0 ? MathF.Sqrt( (float)(sumSq / cnt) ) : 0f;
		float loadDiffAvg = cnt > 0 ? (float)(loadDiffSum / cnt) : 0f;
		_summary = $"yawRms={rms:F2} yawPeak={peak:F2} maxSlip={maxSlip:F2} maxOmega={maxOm:F0} "
			+ $"slipLRmax={maxAbsD:F2} antiphaseFlips={flips} loadDiffAvg={loadDiffAvg:F0}";
		Log.Info( $"[vp] wobbleprobe SUMMARY {_summary}" );

		// 10 Hz downsampled trace for offline comparison
		int stride = Math.Max( 1, (int)(n / MathF.Max( 1f, _dur * 10f )) );
		for ( int i = 0; i < n; i += stride )
			Log.Info( $"[vp] wobbletrace t={_t[i]:F2} yaw={_yaw[i]:F1} kL={_slipL[i]:F2} kR={_slipR[i]:F2} "
				+ $"fzL={_loadL[i]:F0} fzR={_loadR[i]:F0} omL={_omL[i]:F0} omR={_omR[i]:F0}" );
	}

	public override void Report( RunVerdict v )
	{
		v.Add( $"probe {_summary}", "Wobble probe", _summary );
	}
}
