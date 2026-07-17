namespace VehicleProto;

using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Diagnostics;

/// <summary>
/// PHASE-0 PERFORMANCE PROBE (measurement-only, public neutral voice). Establishes a repeatable
/// frametime / physics-step / render-cost / GC / scene-census baseline so a later optimization pass
/// can be measured against real numbers rather than guessed at. It is created ONCE by
/// <see cref="GameBootstrap"/> and sits completely inert until armed through the perf channel on
/// <see cref="VehicleBridge"/> (posted by the editor <c>vp_perf</c> tool) — it applies no forces,
/// changes no dials, and does not sample a single value until a capture is requested.
///
/// TWO INDEPENDENT SIGNALS (the house method —):
///  • FRAME pacing (avg / p50 / p95 / p99 / worst frame time + avg &amp; 1%-low FPS) from per-frame
///    <see cref="RealTime.Delta"/>. CAVEAT: editor-embedded Play is HARD-CAPPED at the desktop vsync
///    (~60 Hz) and not liftable by cvar, so avg frame time floors near 16.7 ms until the scene is
///    genuinely overloaded — the real knee is avg &lt; 60 FPS WITH a collapsing 1%-low.
///  • PHYSICS step time (avg / p99) measured DIRECTLY around the engine solver step via
///    <see cref="IScenePhysicsEvents"/> Pre/Post callbacks + a Stopwatch. This is NOT vsync-masked —
///    it is the true CPU cost of the collider/rigidbody workload, so it is the trustworthy CPU number
///    under the editor cap.
///
/// RENDER counters (draw calls / triangles / objects rendered) come from
/// <see cref="FrameStats.Current"/> and GC counters (Gen0/1/2 collections, bytes allocated) from
/// <see cref="PerformanceStats"/> — both read best-effort inside try/catch so a future engine rename
/// degrades a field to -1 rather than breaking the frame baseline. Scene counts come from
/// <see cref="Scene.GetAllComponents{T}"/> / <c>Scene.GetAllObjects</c>.
///
/// OUTPUT: grep-friendly <c>[vp] PERF …</c> console lines with stable column keys, so two runs diff
/// cleanly. The results are also mirrored into <see cref="VehicleBridge"/> for the tool's status poll.
/// </summary>
public sealed class PerfProbe : Component, IScenePhysicsEvents
{
	/// <summary>Report/route version — bump if the columns or drive law change so an old baseline is
	/// never diffed against a new capture by accident.</summary>
	public const int PerfVersion = 1;

	// A short warmup is dropped at the start of a capture so the play-start / first-view hitch
	// (asset + shader + shadow warm) never poisons the measured window.
	const float WarmupSec = 1.5f;

	// ── capture state ────────────────────────────────────────────────────────────
	bool _capturing;
	string _mode = "idle";          // idle | drive
	float _seconds = 30f;
	float _warmupUntil;
	float _captureStart;
	int _consumedPerfToken = -1;

	readonly List<float> _frameMs = new( 4096 );
	readonly List<double> _physMs = new( 4096 );
	readonly System.Diagnostics.Stopwatch _sw = new();

	// GC baseline latched at window start (deltas reported over the window).
	int _gc0Base, _gc1Base, _gc2Base;
	long _bytesBase;

	VehicleController _driveCar;

	protected override void OnStart()
	{
		// Sync the consumed token so a request already sitting on the bridge at creation time isn't
		// replayed; a fresh request bumps the token and is picked up on the next Update.
		_consumedPerfToken = VehicleBridge.PerfToken;
		VehicleBridge.PerfStatus = "idle";
	}

	// Engine physics-step callbacks fire every solver step; they stay inert (one bool test) until a
	// capture is running, so the probe adds nothing to normal play.
	void IScenePhysicsEvents.PrePhysicsStep()
	{
		if ( _capturing ) _sw.Restart();
	}

	void IScenePhysicsEvents.PostPhysicsStep()
	{
		if ( _capturing && _sw.IsRunning ) _physMs.Add( _sw.Elapsed.TotalMilliseconds );
	}

	protected override void OnUpdate()
	{
		// Pick up a newly posted perf request (census = immediate one-shot; capture = windowed).
		if ( VehicleBridge.PerfToken != _consumedPerfToken && !_capturing )
		{
			_consumedPerfToken = VehicleBridge.PerfToken;
			var req = VehicleBridge.PerfMode;
			if ( req == "census" )
			{
				EmitCensus( "census" );
				VehicleBridge.PerfStatus = "done";
			}
			else
			{
				BeginCapture( req, VehicleBridge.PerfSeconds );
			}
		}

		if ( !_capturing )
			return;

		float now = RealTime.Now;

		// Drop the warmup: keep the window origin pinned to "now" until it ends.
		if ( now < _warmupUntil )
		{
			_captureStart = now;
			_frameMs.Clear();
			_physMs.Clear();
			LatchGcBaseline();
			ApplyDrive( now ); // keep driving through warmup so speed is established when sampling starts
			return;
		}

		float dt = RealTime.Delta;
		if ( dt > 0f )
			_frameMs.Add( dt * 1000f );

		ApplyDrive( now );

		if ( now - _captureStart >= _seconds )
			FinishCapture();
	}

	// ── capture lifecycle ────────────────────────────────────────────────────────

	void BeginCapture( string mode, float seconds )
	{
		_mode = string.Equals( mode, "drive", StringComparison.OrdinalIgnoreCase ) ? "drive" : "idle";
		_seconds = Math.Clamp( seconds, 1f, 300f );
		_capturing = true;
		_warmupUntil = RealTime.Now + WarmupSec;
		_captureStart = RealTime.Now;
		_frameMs.Clear();
		_physMs.Clear();
		LatchGcBaseline();

		_driveCar = _mode == "drive"
			? Scene.GetAllComponents<VehicleController>().FirstOrDefault( c => !c.IsProxy )
			: null;

		VehicleBridge.PerfStatus = "running";
		EmitCensus( _mode ); // census up front so a capture line and its scene context sit together

		Log.Info( $"[vp] PERF START v{PerfVersion} world={World()} mode={_mode} seconds={_seconds:0.#} " +
			$"warmup_s={WarmupSec:0.#} car={( _driveCar.IsValid() ? _driveCar.Definition?.Name ?? "?" : "none" )}" );
	}

	void FinishCapture()
	{
		_capturing = false;

		// Release the car if we were driving it, so keyboard/pilot control resumes cleanly.
		if ( _driveCar.IsValid() )
			_driveCar.InputOverride = null;
		_driveCar = null;

		float elapsed = MathF.Max( RealTime.Now - _captureStart, 0.0001f );
		int n = _frameMs.Count;
		if ( n == 0 )
		{
			Log.Info( $"[vp] PERF SUMMARY v{PerfVersion} world={World()} mode={_mode} frames=0 (no samples)" );
			VehicleBridge.PerfStatus = "done";
			return;
		}

		// Frame pacing.
		_frameMs.Sort();
		float total = 0f, worst = 0f;
		foreach ( var ms in _frameMs ) { total += ms; if ( ms > worst ) worst = ms; }
		float avgMs = total / n;
		float p50 = Percentile( _frameMs, 0.50f );
		float p95 = Percentile( _frameMs, 0.95f );
		float p99 = Percentile( _frameMs, 0.99f );
		float avgFps = avgMs > 0f ? 1000f / avgMs : 0f;

		// 1% low: mean FPS over the slowest 1% of frames (largest frame times — end of the sorted list).
		int lowN = Math.Max( 1, n / 100 );
		float sumLow = 0f;
		for ( int i = n - lowN; i < n; i++ ) sumLow += _frameMs[i];
		float p1LowFps = sumLow > 0f ? lowN * 1000f / sumLow : 0f;

		// Physics step time (vsync-free CPU cost).
		double physAvg = 0, physP99 = 0;
		int pn = _physMs.Count;
		if ( pn > 0 )
		{
			double psum = 0;
			foreach ( var ms in _physMs ) psum += ms;
			physAvg = psum / pn;
			_physMs.Sort();
			int hiN = Math.Max( 1, pn / 100 );
			double sumHi = 0;
			for ( int i = pn - hiN; i < pn; i++ ) sumHi += _physMs[i];
			physP99 = sumHi / hiN;
		}

		Log.Info( $"[vp] PERF SUMMARY v{PerfVersion} world={World()} mode={_mode} dur_s={elapsed:0.0} frames={n} " +
			$"avg_ms={avgMs:0.00} p50_ms={p50:0.00} p95_ms={p95:0.00} p99_ms={p99:0.00} worst_ms={worst:0.00} " +
			$"avg_fps={avgFps:0.0} p1low_fps={p1LowFps:0.0} phys_avg_ms={physAvg:0.000} phys_p99_ms={physP99:0.000} phys_n={pn}" );

		EmitRender();
		EmitGcDelta();

		VehicleBridge.PerfLastSummaryFps = avgFps;
		VehicleBridge.PerfLastP1LowFps = p1LowFps;
		VehicleBridge.PerfLastPhysAvgMs = (float)physAvg;
		VehicleBridge.PerfStatus = "done";
	}

	// Deterministic measurement drive (drive mode only): a bounded weave — steady throttle with a slow
	// sinusoidal steer so the capture exercises moving physics + camera + culling representatively in
	// EITHER world without depending on station geometry. It is pure measurement input staged through
	// the sanctioned InputOverride seam (the same seam the pilot + a human use); it applies no forces
	// and changes no dial, so it can never alter feel outside a capture.
	void ApplyDrive( float now )
	{
		if ( _mode != "drive" || !_driveCar.IsValid() )
			return;

		float t = now - _captureStart;
		float steer = 0.55f * MathF.Sin( t * 0.35f );   // slow left/right weave
		_driveCar.InputOverride = new DriveInputs
		{
			MoveForward = 0.7f,
			Steer = steer,
			Handbrake = false,
		};
	}

	// ── snapshots ────────────────────────────────────────────────────────────────

	void LatchGcBaseline()
	{
		try
		{
			_gc0Base = PerformanceStats.Gen0Collections;
			_gc1Base = PerformanceStats.Gen1Collections;
			_gc2Base = PerformanceStats.Gen2Collections;
			_bytesBase = PerformanceStats.BytesAllocated;
		}
		catch { _gc0Base = _gc1Base = _gc2Base = -1; _bytesBase = -1; }
	}

	void EmitGcDelta()
	{
		try
		{
			int g0 = PerformanceStats.Gen0Collections - _gc0Base;
			int g1 = PerformanceStats.Gen1Collections - _gc1Base;
			int g2 = PerformanceStats.Gen2Collections - _gc2Base;
			double allocMb = _bytesBase >= 0 ? (PerformanceStats.BytesAllocated - _bytesBase) / (1024.0 * 1024.0) : -1;
			Log.Info( $"[vp] PERF GC gen0={g0} gen1={g1} gen2={g2} alloc_mb={allocMb:0.00}" );
		}
		catch ( Exception e )
		{
			Log.Info( $"[vp] PERF GC unavailable ({e.Message}) — PerformanceStats not readable" );
		}
	}

	void EmitRender()
	{
		try
		{
			var fs = FrameStats.Current;
			Log.Info( $"[vp] PERF RENDER draws={(long)fs.DrawCalls} tris={(long)fs.TrianglesRendered} " +
				$"objs_rendered={(long)fs.ObjectsRendered} objs_precull={(long)fs.ObjectsPreCull} " +
				$"shadowmaps={(long)fs.ShadowMaps} shadowlights={(long)fs.ShadowedLightsInView}" );
		}
		catch ( Exception e )
		{
			Log.Info( $"[vp] PERF RENDER unavailable ({e.Message}) — FrameStats not readable" );
		}
	}

	/// <summary>Static scene census — the draw-batch / object-count picture. Reported at the start of
	/// every capture and standalone via the census request. Counts are always-available scene truth
	/// (independent of the vsync-masked frame metrics).</summary>
	void EmitCensus( string tag )
	{
		int objects = CountObjects();
		int renderers = CountSafe<ModelRenderer>();
		int skinned = CountSafe<SkinnedModelRenderer>();
		int colliders = CountSafe<Collider>();
		int box = CountSafe<BoxCollider>();
		int sphere = CountSafe<SphereCollider>();
		int wheels = CountSafe<VehicleWheel>();

		// tris/draws pulled alongside so the census line carries the render totals at census time too.
		long tris = -1, draws = -1;
		try { var fs = FrameStats.Current; tris = (long)fs.TrianglesRendered; draws = (long)fs.DrawCalls; }
		catch { /* left -1 */ }

		Log.Info( $"[vp] PERF CENSUS world={World()} tag={tag} objects={objects} renderers={renderers} " +
			$"skinned={skinned} colliders={colliders} box={box} sphere={sphere} wheels={wheels} " +
			$"tris={tris} draws={draws}" );
	}

	int CountObjects()
	{
		try { return Scene.GetAllObjects( true ).Count(); }
		catch { return -1; }
	}

	int CountSafe<T>() where T : Component
	{
		try { return Scene.GetAllComponents<T>().Count(); }
		catch { return -1; }
	}

	static float Percentile( List<float> sortedAsc, float q )
	{
		int n = sortedAsc.Count;
		if ( n == 0 ) return 0f;
		int i = Math.Clamp( (int)MathF.Ceiling( q * n ) - 1, 0, n - 1 );
		return sortedAsc[i];
	}

	static string World() => string.IsNullOrEmpty( VehicleBridge.World ) ? "?" : VehicleBridge.World;
}
