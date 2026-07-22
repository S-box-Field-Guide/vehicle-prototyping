using System;
using System.Collections.Generic;
using System.Text;

namespace VehicleProto;

/// <summary>
/// LIVE DEBUG INSTRUMENT (2026-07-21, ramp-hitch hunt; v2 = round 6). High-rate flight recorder:
/// records EVERY fixed tick (car kinematics + per-wheel suspension) and EVERY render frame
/// (frame dt + interpolated car position + chase-camera position) into preallocated in-memory
/// buffers, then dumps one CSV to FileSystem.Data on capture end. Nothing is logged to the
/// console during capture and the hot path allocates nothing after warmup, so the instrument
/// cannot cause the stutter it is trying to measure. Toggle with the vp_ramptrace convar
/// (set 1 to arm, 0 to dump); a capture also auto-dumps after MaxCaptureSeconds.
///
/// ROUND-6 UPGRADE (offline analysis of the round-5 captures, 2026-07-21): on the ramp face the
/// car's raw pose advances in an alternating 0.63 m / 0.23 m per-tick RATCHET (40% displacement
/// deficit vs SpeedMs·dt; spatial period ~0.86 m ≈ the kicker segment-box pitch) while
/// Rigidbody.Velocity stays glassy-smooth (+0.03/tick, no alternation at 3 decimals) — flat
/// ground is exact to 1 mm, airborne is exact, ONLY the grounded face climb ratchets. Rendering
/// interpolates that ratchet faithfully, which IS the owner's visible "car sticks on the ramp"
/// (worst at high refresh; fps_max 50 masks it). Suspect: ghost/speculative contacts against the
/// INTERNAL faces of the overlapping segment boxes clamping integration without touching
/// velocity. The new columns discriminate the remaining hypotheses:
///   vx,vy,vz      — velocity components: is velocity really untouched through the ratchet?
///   bx,by,bz      — PhysicsBody.Position read directly: does the BODY ratchet, or only the
///                   GameObject write-back layer? (solver bug vs engine write-back bug)
///   cn,cnx/y/z,cname — chassis collision events this tick (via RampContactProbe): WHO touches
///                   during the climb, with what normal? Ghost-contact hypothesis predicts
///                   near-horizontal normals from "KickerSeg" boxes. One tick of latency.
///   rt            — RealTime.Now in both row types: fixed-grid vs wall-clock scheduling jitter.
///   R rows ix,iy  — interpolated horizontal position: quantifies the RENDERED judder at the
///                   owner's real refresh rate (run with fps_max 0 — round-5 captures were all
///                   accidentally at fps_max 50, which masks the visual).
///
/// What the two row types answer:
///   F rows (fixed tick): is the hitch REAL car motion (speed/pitch/suspension discontinuity)?
///   R rows (render frame): is it a FRAME-TIME spike (dt), an interpolation fault
///     (interpolated pos vs the F-row bracket), or the chase camera (cam distance dynamics)?
/// </summary>
public sealed class RampTraceRecorder : Component
{
	[ConVar( "vp_ramptrace" )]
	public static bool Armed { get; set; }

	/// <summary>TESTING MODE (owner order 2026-07-21): auto-arm a capture at play start and
	/// re-arm after every dump, so EVERY owner run is recorded without touching the convar
	/// (the green-kicker "acted like a collision" report arrived with no telemetry because the
	/// recorder was off). Captures chain in 120 s segments; play-stop still teardown-dumps.
	/// ⚠ PRE-DEPLOY: set this back to FALSE before the next publish (tracked in
	/// docs/PRE-DEPLOY-NOTES.md).</summary>
	public const bool TestingAutoArm = true;

	public VehicleController Target { get; set; }

	const int MaxFixedSamples = 120 * 50;   // 120 s at 50 Hz
	const int MaxFrameSamples = 120 * 250;  // 120 s at up to 250 fps
	const float MaxCaptureSeconds = 120f;

	struct FixedSample
	{
		public float T, Rt, X, Y, Z, Vx, Vy, Vz, Speed, Pitch;
		public float Bx, By, Bz;
		public byte G0, G1, G2, G3;
		public float L0, L1, L2, L3;
		public float S0, S1, S2, S3;
		public int ContactCount;
		public Vector3 ContactNormal;
		public string ContactOther;
	}

	struct FrameSample
	{
		public float T, Rt, Dt, CarX, CarY, CarZ, CamZ, CamDist;
	}

	List<FixedSample> _fixed;
	List<FrameSample> _frames;
	bool _capturing;
	float _startedAt;
	VehicleCamera _camera;
	RampContactProbe _probe;

	protected override void OnFixedUpdate()
	{
		if ( TestingAutoArm && !Armed )
			Armed = true;   // testing mode: every run records; convar 0 still forces a dump (then re-arms)

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
		var rigidbody = car.Components.Get<Rigidbody>();
		var vel = rigidbody.IsValid() ? rigidbody.Velocity * Units.UnitsToMeters : Vector3.Zero;
		// PhysicsBody.Position bypasses the GameObject write-back: if bx/by/bz advance at v·dt
		// while x/y/z ratchet, the theft is in the engine's UpdateTransformFromBody layer; if
		// both ratchet identically, it's the solver itself.
		var body = rigidbody.IsValid() ? rigidbody.PhysicsBody : null;
		var bpos = body is not null ? body.Position * Units.UnitsToMeters : pos;

		var s = new FixedSample
		{
			T = Time.Now,
			Rt = RealTime.Now,
			X = pos.x, Y = pos.y, Z = pos.z,
			Vx = vel.x, Vy = vel.y, Vz = vel.z,
			Speed = Target.SpeedMs,
			Pitch = car.WorldRotation.Pitch(),
			Bx = bpos.x, By = bpos.y, Bz = bpos.z,
			ContactOther = "",
		};

		if ( _probe.IsValid() )
		{
			var (count, normal, other) = _probe.Drain();
			s.ContactCount = count;
			s.ContactNormal = normal;
			s.ContactOther = other;
		}

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
			Rt = RealTime.Now,
			Dt = Time.Delta,
			CarX = carPos.x,
			CarY = carPos.y,
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
		if ( Target.IsValid() && !Target.GameObject.Components.TryGet<RampContactProbe>( out _probe ) )
			_probe = Target.GameObject.Components.Create<RampContactProbe>();
		_startedAt = Time.Now;
		_capturing = true;
		Log.Info( $"[vp] ramptrace v2 ARMED at t={Time.Now:F2} (dump: set vp_ramptrace 0, or auto after {MaxCaptureSeconds:F0}s)" );
	}

	void Dump()
	{
		_capturing = false;
		Armed = false;

		var sb = new StringBuilder( (_fixed.Count + _frames.Count) * 160 + 512 );
		sb.AppendLine( "type,t,rt,dt,x,y,z,vx,vy,vz,speed,pitch,bx,by,bz,g0,l0,s0,g1,l1,s1,g2,l2,s2,g3,l3,s3,cn,cnx,cny,cnz,cname,camz,camdist" );
		foreach ( var s in _fixed )
		{
			string cname = string.IsNullOrEmpty( s.ContactOther ) ? "" : s.ContactOther.Replace( ',', ';' );
			sb.AppendLine( FormattableString.Invariant(
				$"F,{s.T:F4},{s.Rt:F4},,{s.X:F3},{s.Y:F3},{s.Z:F3},{s.Vx:F3},{s.Vy:F3},{s.Vz:F3},{s.Speed:F3},{s.Pitch:F2},{s.Bx:F3},{s.By:F3},{s.Bz:F3},{s.G0},{s.L0:F0},{s.S0:F4},{s.G1},{s.L1:F0},{s.S1:F4},{s.G2},{s.L2:F0},{s.S2:F4},{s.G3},{s.L3:F0},{s.S3:F4},{s.ContactCount},{s.ContactNormal.x:F3},{s.ContactNormal.y:F3},{s.ContactNormal.z:F3},{cname},," ) );
		}
		foreach ( var f in _frames )
			sb.AppendLine( FormattableString.Invariant(
				$"R,{f.T:F4},{f.Rt:F4},{f.Dt:F5},{f.CarX:F3},{f.CarY:F3},{f.CarZ:F3},,,,,,,,,,,,,,,,,,,,,,,,,,{f.CamZ:F3},{f.CamDist:F3}" ) );

		string name = FormattableString.Invariant( $"ramptrace2-{DateTime.Now:HHmmss}.csv" );
		FileSystem.Data.WriteAllText( name, sb.ToString() );
		Log.Info( $"[vp] ramptrace v2 DUMPED {name}: {_fixed.Count} fixed + {_frames.Count} frame samples" );

		if ( _probe.IsValid() )
		{
			_probe.Destroy();
			_probe = null;
		}

		_fixed = null;
		_frames = null;
	}
}
