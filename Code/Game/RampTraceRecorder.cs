using System;
using System.Collections.Generic;
using System.Text;

namespace VehicleProto;

/// <summary>
/// LIVE DEBUG INSTRUMENT (2026-07-21, ramp-hitch hunt round 5). High-rate flight recorder for
/// the owner-reported ramp hitch that has survived every model-driven fix: records EVERY fixed
/// tick (car kinematics + per-wheel suspension) and EVERY render frame (frame dt + interpolated
/// car position + chase-camera position) into preallocated in-memory buffers, then dumps one CSV
/// to FileSystem.Data on capture end. Nothing is logged to the console during capture and the
/// hot path allocates nothing after warmup, so the instrument cannot cause the stutter it is
/// trying to measure. Toggle with the vp_ramptrace convar (set 1 to arm, 0 to dump); a capture
/// also auto-dumps after MaxCaptureSeconds so a forgotten toggle cannot grow unbounded.
///
/// What the two row types answer:
///   F rows (fixed tick): is the hitch REAL car motion (speed/pitch/suspension discontinuity)?
///   R rows (render frame): is it a FRAME-TIME spike (dt column), an interpolation fault
///     (interpolated z vs the F-row z bracket), or the chase camera (cam distance dynamics)?
/// </summary>
public sealed class RampTraceRecorder : Component
{
	[ConVar( "vp_ramptrace" )]
	public static bool Armed { get; set; }

	public VehicleController Target { get; set; }

	const int MaxFixedSamples = 120 * 50;   // 120 s at 50 Hz
	const int MaxFrameSamples = 120 * 250;  // 120 s at up to 250 fps
	const float MaxCaptureSeconds = 120f;

	struct FixedSample
	{
		public float T, X, Y, Z, Speed, Pitch;
		public byte G0, G1, G2, G3;
		public float L0, L1, L2, L3;
		public float S0, S1, S2, S3;
	}

	struct FrameSample
	{
		public float T, Dt, CarZ, CamZ, CamDist;
	}

	List<FixedSample> _fixed;
	List<FrameSample> _frames;
	bool _capturing;
	float _startedAt;
	VehicleCamera _camera;

	protected override void OnFixedUpdate()
	{
		if ( Armed && !_capturing )
			Begin();
		else if ( !Armed && _capturing )
		{
			Dump();
			return;
		}

		if ( !_capturing || Target is null || !Target.IsValid() )
			return;

		if ( Time.Now - _startedAt > MaxCaptureSeconds || _fixed.Count >= MaxFixedSamples )
		{
			Dump();
			return;
		}

		var car = Target.GameObject;
		var pos = car.WorldPosition * Units.UnitsToMeters;
		var s = new FixedSample
		{
			T = Time.Now,
			X = pos.x, Y = pos.y, Z = pos.z,
			Speed = Target.SpeedMs,
			Pitch = car.WorldRotation.Pitch(),
		};

		var wheels = Target.Wheels;
		if ( wheels.Count >= 4 )
		{
			s.G0 = (byte)(wheels[0].IsGrounded ? 1 : 0); s.L0 = wheels[0].Load; s.S0 = wheels[0].SuspensionLength;
			s.G1 = (byte)(wheels[1].IsGrounded ? 1 : 0); s.L1 = wheels[1].Load; s.S1 = wheels[1].SuspensionLength;
			s.G2 = (byte)(wheels[2].IsGrounded ? 1 : 0); s.L2 = wheels[2].Load; s.S2 = wheels[2].SuspensionLength;
			s.G3 = (byte)(wheels[3].IsGrounded ? 1 : 0); s.L3 = wheels[3].Load; s.S3 = wheels[3].SuspensionLength;
		}

		_fixed.Add( s );
	}

	protected override void OnUpdate()
	{
		if ( !_capturing || Target is null || !Target.IsValid() )
			return;

		if ( _frames.Count >= MaxFrameSamples )
			return;

		var carPos = Target.GameObject.WorldPosition * Units.UnitsToMeters;
		float camZ = 0f, camDist = 0f;
		if ( _camera.IsValid() )
		{
			var camPos = _camera.WorldPosition * Units.UnitsToMeters;
			camZ = camPos.z;
			camDist = camPos.Distance( carPos );
		}

		_frames.Add( new FrameSample
		{
			T = Time.Now,
			Dt = Time.Delta,
			CarZ = carPos.z,
			CamZ = camZ,
			CamDist = camDist,
		} );
	}

	protected override void OnDisabled()
	{
		// Scene teardown (play stop) must not lose a capture in progress: dump whatever we have.
		if ( _capturing && _fixed is { Count: > 0 } )
		{
			try { Dump(); }
			catch ( Exception e ) { Log.Warning( $"[vp] ramptrace teardown dump failed: {e.Message}" ); }
		}
	}

	void Begin()
	{
		_fixed = new List<FixedSample>( MaxFixedSamples );
		_frames = new List<FrameSample>( MaxFrameSamples );
		_camera = Scene.GetAllComponents<VehicleCamera>().FirstOrDefault();
		_startedAt = Time.Now;
		_capturing = true;
		Log.Info( $"[vp] ramptrace ARMED at t={Time.Now:F2} (dump: set vp_ramptrace 0, or auto after {MaxCaptureSeconds:F0}s)" );
	}

	void Dump()
	{
		_capturing = false;
		Armed = false;

		var sb = new StringBuilder( (_fixed.Count + _frames.Count) * 96 + 256 );
		sb.AppendLine( "type,t,dt,x,y,z,speed,pitch,g0,l0,s0,g1,l1,s1,g2,l2,s2,g3,l3,s3,camz,camdist" );
		foreach ( var s in _fixed )
			sb.AppendLine( FormattableString.Invariant(
				$"F,{s.T:F4},,{s.X:F3},{s.Y:F3},{s.Z:F3},{s.Speed:F3},{s.Pitch:F2},{s.G0},{s.L0:F0},{s.S0:F4},{s.G1},{s.L1:F0},{s.S1:F4},{s.G2},{s.L2:F0},{s.S2:F4},{s.G3},{s.L3:F0},{s.S3:F4},," ) );
		foreach ( var f in _frames )
			sb.AppendLine( FormattableString.Invariant(
				$"R,{f.T:F4},{f.Dt:F5},,,{f.CarZ:F3},,,,,,,,,,,,,,,{f.CamZ:F3},{f.CamDist:F3}" ) );

		string name = FormattableString.Invariant( $"ramptrace-{DateTime.Now:HHmmss}.csv" );
		FileSystem.Data.WriteAllText( name, sb.ToString() );
		Log.Info( $"[vp] ramptrace DUMPED {name}: {_fixed.Count} fixed + {_frames.Count} frame samples" );

		_fixed = null;
		_frames = null;
	}
}
