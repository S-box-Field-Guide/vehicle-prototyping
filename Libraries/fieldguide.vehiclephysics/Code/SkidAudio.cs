namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Placeholder skid / traction-loss audio: one shared looping tyre-screech per car, driven by the
/// worst wheel slip (longitudinal spin/lockup OR lateral slide). Created on every car root by
/// <see cref="VehicleFactory.Spawn"/> alongside <see cref="EngineAudio"/> — it exists so handbraking,
/// a power-slide, or a locked-up stop gives audible feedback, and it is meant to be replaced by a
/// proper per-wheel surface-aware model later (per-car, not per-wheel, is fine for v1).
///
/// The event is a 3D positional <c>.sound</c> under <c>Assets/sounds/skid/</c>. SoundEvent has no loop
/// flag, so the loop is held code-side: keep the handle, follow the car, and re-trigger when it stops.
/// The loop is silent below a slip threshold; above it, volume and pitch ride slip magnitude so a
/// light scrub whispers and a full slide screeches.
///
/// One console dial: <c>vp_skid_volume</c> — master scale (0 = mute), applied live (matches the
/// <c>vp_engine_*</c> naming).
/// </summary>
public sealed class SkidAudio : Component
{
	/// <summary>Master skid-volume scale. 1 = full, 0 = mute. Read every frame so it takes effect live.</summary>
	[ConVar( "vp_skid_volume" )] public static float MasterVolume { get; set; } = 1f;

	const string SkidEvent = "sounds/skid/skid_asphalt_loop.sound";

	// Slip magnitude below this = no skid sound (rolling / gentle cornering); at SkidFull the loop is
	// at full volume. Slip metric = max over grounded wheels of max( |SlipRatio|, |SlipAngle|/AngleRef ).
	const float SlipThreshold = 0.40f;
	const float SkidFull = 1.10f;
	const float AngleRef = 0.70f;   // rad (~40 deg) lateral slip that reads as a full slide

	// Pitch rides slip a touch so a bigger slide sounds more frantic; volume ceiling keeps the screech
	// sitting under the engine rather than dominating it.
	const float MinPitch = 0.90f;
	const float MaxPitch = 1.15f;
	const float MaxVolume = 0.9f;

	VehicleController _controller;
	SoundHandle _handle;
	TimeSince _log;

	protected override void OnStart()
	{
		_controller = Components.Get<VehicleController>();
	}

	protected override void OnUpdate()
	{
		if ( _controller is null )
			return;

		float slip = MasterVolume > 0f ? SkidAmount() : 0f;

		// Below threshold (or muted): stop the loop and release the handle; it restarts when a skid returns.
		if ( slip <= SlipThreshold )
		{
			StopHandle();
			return;
		}

		// Hold the loop alive: (re-)trigger whenever nothing is playing (first skid or after the finite
		// sample reaches its end). Positional so the sound tracks the car in the world.
		if ( _handle is null || !_handle.IsPlaying )
			_handle = Sound.Play( SkidEvent, WorldPosition );

		if ( _handle is null )
			return;

		float t = Math.Clamp( (slip - SlipThreshold) / MathF.Max( 0.01f, SkidFull - SlipThreshold ), 0f, 1f );
		_handle.Position = WorldPosition;
		_handle.Volume = t * MaxVolume * MathF.Max( 0f, MasterVolume );
		_handle.Pitch = MathX.Lerp( MinPitch, MaxPitch, t );

		// Low-rate diagnostic (1 Hz while skidding): the slip magnitude and the vol/pitch it drives.
		if ( _log > 1f )
		{
			_log = 0f;
			Log.Info( $"[vp] skid audio: slip {slip:F2} vol {_handle.Volume:F2} pitch {_handle.Pitch:F2}" );
		}
	}

	protected override void OnDestroy() => StopHandle();

	void StopHandle()
	{
		_handle?.Stop( 0.12f );
		_handle = null;
	}

	/// <summary>Worst tyre slip across grounded wheels: the larger of |SlipRatio| (spin / lockup) and
	/// |SlipAngle|/AngleRef (lateral slide). 0 = gripping, ~1 = a full slide or a locked wheel.</summary>
	float SkidAmount()
	{
		float worst = 0f;
		foreach ( var wheel in _controller.Wheels )
		{
			if ( wheel is null || !wheel.IsGrounded )
				continue;

			float lng = MathF.Abs( wheel.SlipRatio );
			float lat = MathF.Abs( wheel.SlipAngle ) / AngleRef;
			worst = MathF.Max( worst, MathF.Max( lng, lat ) );
		}
		return worst;
	}
}
