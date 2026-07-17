namespace VehicleProto;

/// <summary>
/// Engine audio: a positional engine loop that follows the car, pitched by live RPM and swelled by
/// throttle. Created on every car root by <see cref="VehicleFactory.Spawn"/>. Two models share this
/// component:
///
///  - LAYERED (real recorded sets): a class with both <see cref="CarDefinition.EngineSoundEvent"/>
///    (low/idle layer) and <see cref="CarDefinition.EngineSoundEventHigh"/> (high/rev layer) blends
///    the two by normalized RPM. The low layer carries idle and fades out as revs climb; the high
///    layer fades in over the top (an equal-power crossfade, so combined loudness stays steady). The
///    key trait: EACH layer only pitch-shifts a NARROW band around its own recorded pitch, so no
///    layer is ever stretched far from how it was recorded — that far stretch is what makes a single
///    loop screech at redline.
///
///  - LEGACY single loop: a class with no high layer (e.g. the kart's single synth purr) plays one
///    handle across a wider pitch sweep, exactly as before. The <c>vp_engine_sound</c> dev override
///    also forces this single-loop path so any one loop can be auditioned on every car.
///
/// SoundEvent has no loop flag, so each loop is held code-side: keep the handle, follow the car with
/// it, and re-trigger the moment it stops playing. Volume rides throttle from an idle floor to full.
///
/// Two console dials tune it live:
///   <c>vp_engine_sound</c> — dev override: empty = per-class default; a short name or a full event
///     path forces one single loop on every car (single-loop mode).
///   <c>vp_engine_volume</c> — master scale (0 = mute), applied live.
/// </summary>
public sealed class EngineAudio : Component
{
	/// <summary>Dev override for which engine loop plays. Empty (default) = each car uses its own
	/// per-class loop(s). A short name (<c>a b c real_low real_high real_truck_low real_truck_high</c>)
	/// or a full event path forces that single loop on every car for A/B listening, in single-loop
	/// mode. Read every frame, so changing it in the console swaps the running loop live.</summary>
	[ConVar( "vp_engine_sound" )] public static string SoundOverride { get; set; } = "";

	/// <summary>Master engine-volume scale, applied on top of the throttle swell. 1 = full, 0 = mute.
	/// Read every frame so it takes effect live.</summary>
	[ConVar( "vp_engine_volume" )] public static float MasterVolume { get; set; } = 1f;

	// Legacy single-loop pitch band: idle..redline RPM maps onto this, then scaled by class base pitch.
	// Narrow so redline reads as one engine rather than a chipmunk sweep.
	const float LegacyMinPitch = 0.7f;
	const float LegacyMaxPitch = 1.3f;

	// Layered crossfade bands over normalized RPM t (0 = idle, 1 = redline). The low layer fades out
	// across [LowFadeStart..LowFadeEnd]; the high layer fades in across [HighFadeStart..HighFadeEnd].
	// They overlap in the middle, where both play (equal-power blended).
	const float LowFadeStart = 0.0f;
	const float LowFadeEnd = 0.7f;
	const float HighFadeStart = 0.3f;
	const float HighFadeEnd = 1.0f;

	// Per-layer pitch bands — each stays close to 1.0 (its recorded pitch) so no layer is stretched
	// far enough to screech. Mapped across the full RPM sweep, but each layer is only audible over
	// its own fade band, so its extreme pitch ends are barely heard.
	const float LowLayerMinPitch = 0.9f;
	const float LowLayerMaxPitch = 1.25f;
	const float HighLayerMinPitch = 0.85f;
	const float HighLayerMaxPitch = 1.2f;

	// Throttle volume swell: idle floor (so a coasting car is still audible) up to full at pinned throttle.
	const float IdleVolume = 0.55f;
	const float FullVolume = 1.0f;

	VehicleController _controller;

	// Low/idle layer (also the sole handle in single-loop mode); high/rev layer (layered mode only).
	SoundHandle _low;
	SoundHandle _high;
	string _lowEvent;
	string _highEvent; // null/empty => single-loop mode is active
	TimeSince _log;

	protected override void OnStart()
	{
		_controller = Components.Get<VehicleController>();

		ResolveEvents( SoundOverride, _controller?.Definition, out string lo, out string hi );
		float basePitch = _controller?.Definition?.EnginePitchBase ?? 1f;
		// The override persists in editor config across sessions, so a stale dev value silently
		// masks every per-class loop on every car — warn loudly whenever it is in effect.
		if ( !string.IsNullOrEmpty( SoundOverride ) )
			Log.Warning( $"[vp] engine audio: vp_engine_sound override '{SoundOverride}' ACTIVE - per-class loops suppressed on ALL cars (clear with: vp_engine_sound \"\")" );
		if ( string.IsNullOrEmpty( hi ) )
			Log.Info( $"[vp] engine audio: single loop {lo} pitch {LegacyMinPitch * basePitch:F2}-{LegacyMaxPitch * basePitch:F2}" );
		else
			Log.Info( $"[vp] engine audio: layered low={lo} high={hi} basePitch {basePitch:F2}" );
	}

	protected override void OnUpdate()
	{
		if ( _controller is null )
			return;

		// Master mute: stop both loops and release the handles; they restart when volume returns.
		if ( MasterVolume <= 0f )
		{
			StopHandles();
			return;
		}

		// Live loop switch: if the per-class default or the console override selects a different
		// low/high pairing, drop the handles so the block below re-triggers with the new events.
		ResolveEvents( SoundOverride, _controller.Definition, out string lowEv, out string highEv );
		if ( lowEv != _lowEvent || highEv != _highEvent )
			StopHandles();

		float t = NormalizedRpm();
		float basePitch = _controller.Definition?.EnginePitchBase ?? 1f;
		float swell = VolumeForThrottle();
		bool layered = !string.IsNullOrEmpty( highEv );

		// Hold the low/idle loop alive: (re-)trigger whenever nothing is playing (first frame, after a
		// mute, or when the finite sample reaches its end). Positional so it tracks the car.
		if ( _low is null || !_low.IsPlaying )
		{
			_low = Sound.Play( lowEv, WorldPosition );
			_lowEvent = lowEv;
			_highEvent = highEv;
		}

		if ( _low is not null )
		{
			_low.Position = WorldPosition;
			if ( layered )
			{
				_low.Pitch = basePitch * MathX.Lerp( LowLayerMinPitch, LowLayerMaxPitch, t );
				_low.Volume = swell * LowLayerGain( t );
			}
			else
			{
				// Single-loop model: one handle, wider pitch sweep.
				_low.Pitch = basePitch * MathX.Lerp( LegacyMinPitch, LegacyMaxPitch, t );
				_low.Volume = swell;
			}
		}

		if ( layered )
		{
			// High/rev layer: same hold-alive + positional-follow pattern, blended in over the top.
			if ( _high is null || !_high.IsPlaying )
			{
				_high = Sound.Play( highEv, WorldPosition );
				_highEvent = highEv;
			}

			if ( _high is not null )
			{
				_high.Position = WorldPosition;
				_high.Pitch = basePitch * MathX.Lerp( HighLayerMinPitch, HighLayerMaxPitch, t );
				_high.Volume = swell * HighLayerGain( t );
			}
		}
		else if ( _high is not null )
		{
			// Switched from layered to single-loop (e.g. override engaged): drop the stale high layer.
			_high.Stop( 0.05f );
			_high = null;
		}

		// Low-rate diagnostic (1 Hz while under throttle or moving): live RPM, the pitch/volume of each
		// active layer, and whether the loops are playing. Mirrors the controller's telemetry cadence.
		if ( _log > 1f && (_controller.Throttle > 0f || _controller.SpeedMs > 0.5f) )
		{
			_log = 0f;
			float rpm = _controller.Drivetrain?.Rpm ?? 0f;
			if ( layered )
				Log.Info( $"[vp] engine audio: rpm {rpm:F0} t {t:F2} lo(pitch {_low?.Pitch ?? 0:F2} vol {_low?.Volume ?? 0:F2}) hi(pitch {_high?.Pitch ?? 0:F2} vol {_high?.Volume ?? 0:F2}) playing {((_low?.IsPlaying ?? false) ? 1 : 0)}" );
			else
				Log.Info( $"[vp] engine audio: rpm {rpm:F0} pitch {_low?.Pitch ?? 0:F2} vol {_low?.Volume ?? 0:F2} playing {((_low?.IsPlaying ?? false) ? 1 : 0)}" );
		}
	}

	protected override void OnDestroy() => StopHandles();

	void StopHandles()
	{
		_low?.Stop( 0.05f );
		_low = null;
		_high?.Stop( 0.05f );
		_high = null;
	}

	/// <summary>Normalized RPM in 0..1 (idle..redline), read from the car's own definition so a
	/// low-revving truck and a high-revving kart both map their full band onto 0..1.</summary>
	float NormalizedRpm()
	{
		var def = _controller.Definition;
		var drivetrain = _controller.Drivetrain;
		if ( def is null || drivetrain is null )
			return 0f;

		float span = MathF.Max( 1f, def.RedlineRpm - def.IdleRpm );
		return Math.Clamp( (drivetrain.Rpm - def.IdleRpm) / span, 0f, 1f );
	}

	/// <summary>Low-layer weight: full at idle, smoothly fading to 0 by <see cref="LowFadeEnd"/>.
	/// Equal-power normalized against the high layer so their combined acoustic power stays ~constant
	/// through the crossfade (no dip or bump in perceived loudness mid-blend).</summary>
	static float LowLayerGain( float t )
	{
		float lowRaw = 1f - Smooth01( LowFadeStart, LowFadeEnd, t );
		float highRaw = Smooth01( HighFadeStart, HighFadeEnd, t );
		return EqualPower( lowRaw, highRaw, lowRaw );
	}

	/// <summary>High-layer weight: 0 until <see cref="HighFadeStart"/>, smoothly rising to full at
	/// redline; equal-power normalized against the low layer.</summary>
	static float HighLayerGain( float t )
	{
		float lowRaw = 1f - Smooth01( LowFadeStart, LowFadeEnd, t );
		float highRaw = Smooth01( HighFadeStart, HighFadeEnd, t );
		return EqualPower( lowRaw, highRaw, highRaw );
	}

	/// <summary>Normalize <paramref name="which"/> so that the two raw weights combine to unit power
	/// (lowGain² + highGain² = 1), i.e. an equal-power crossfade rather than a linear one.</summary>
	static float EqualPower( float lowRaw, float highRaw, float which )
	{
		float norm = MathF.Sqrt( (lowRaw * lowRaw) + (highRaw * highRaw) );
		return norm < 1e-4f ? 0f : which / norm;
	}

	/// <summary>Smoothstep of x within [edge0, edge1] → 0..1 (0 below edge0, 1 above edge1).</summary>
	static float Smooth01( float edge0, float edge1, float x )
	{
		float s = Math.Clamp( (x - edge0) / MathF.Max( 1e-4f, edge1 - edge0 ), 0f, 1f );
		return s * s * (3f - (2f * s));
	}

	/// <summary>Idle floor up to full at pinned throttle, then scaled by the master volume dial so
	/// lift-off is an audible dip rather than silence.</summary>
	float VolumeForThrottle()
	{
		float swell = MathX.Lerp( IdleVolume, FullVolume, Math.Clamp( _controller.Throttle, 0f, 1f ) );
		return swell * MathF.Max( 0f, MasterVolume );
	}

	/// <summary>Resolve the low/high event paths for this frame. The <c>vp_engine_sound</c> override
	/// wins when set — it forces a single loop (high = null) on every car for A/B listening. Otherwise
	/// the car plays its own <see cref="CarDefinition.EngineSoundEvent"/> (low) and, when it has one,
	/// <see cref="CarDefinition.EngineSoundEventHigh"/> (high, = layered mode). The <c>.sound</c>
	/// extension is required — the runtime resolves the event by its full resource path.</summary>
	static void ResolveEvents( string overrideVal, CarDefinition def, out string low, out string high )
	{
		string o = (overrideVal ?? "").Trim();
		if ( o.Length > 0 )
		{
			low = ResolveOverride( o );
			high = null; // override always forces single-loop mode
			return;
		}

		low = def?.EngineSoundEvent ?? "sounds/engine/engine_real_low.sound";
		string hi = def?.EngineSoundEventHigh;
		high = string.IsNullOrWhiteSpace( hi ) ? null : hi;
	}

	/// <summary>Map a <c>vp_engine_sound</c> override value (short name or full path) to an event
	/// path, ensuring the required <c>.sound</c> extension.</summary>
	static string ResolveOverride( string o )
	{
		string lo = o.ToLowerInvariant();
		// A full/partial path override is used verbatim (just ensure the required .sound extension).
		if ( lo.Contains( '/' ) )
			return lo.EndsWith( ".sound" ) ? o : o + ".sound";

		return lo switch
		{
			"b" => "sounds/engine/engine_b_sport_purr.sound",
			"low" or "real_low" => "sounds/engine/engine_real_low.sound",
			"high" or "real_high" => "sounds/engine/engine_real_high.sound",
			"truck_low" or "real_truck_low" => "sounds/engine/engine_real_truck_low.sound",
			"truck_high" or "real_truck_high" => "sounds/engine/engine_real_truck_high.sound",
			_ => "sounds/engine/engine_real_low.sound",
		};
	}
}
