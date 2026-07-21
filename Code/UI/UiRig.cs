namespace VehicleProto;

/// <summary>
/// Single entry point that builds and wires the whole HUD stack. GameBootstrap calls
/// <see cref="Mount"/> in place of hand-wiring each panel, so the panel roster lives in
/// one place. All four PanelComponents share one ScreenPanel GameObject (ui-razor.md).
/// </summary>
public static class UiRig
{
	/// <summary>
	/// Create the HUD host under <paramref name="hudGo"/> (or a fresh child) and wire every
	/// panel to <paramref name="target"/>. Resets the static UI buffers so nothing survives
	/// a Play→Stop→Play cycle in the editor.
	/// </summary>
	public static void Mount( GameObject hudGo, VehicleController target )
	{
		UiState.Reset();
		UiFeed.Reset();
		TuneRenameState.Reset();

		hudGo.Components.GetOrCreate<ScreenPanel>();

		var hud = hudGo.Components.Create<VehicleHud>();
		hud.Target = target;

		var telemetry = hudGo.Components.Create<TelemetryPanel>();
		telemetry.Target = target;

		// Live launch timer for the PLAYER — feeds the telemetry TIMING card during free driving
		// (the pilot only feeds it during scripted maneuvers). A plain always-on Component, not a
		// panel; it follows the active car like the panels do (Mount + Retarget below).
		var playerTiming = hudGo.Components.Create<PlayerTiming>();
		playerTiming.Target = target;

		// Drive-mode session persistence (owner request 2026-07-21): watches Target.Assists and
		// mirrors any change into UserSettings.AssistLevel. Same always-on Component pattern as
		// PlayerTiming above (not a panel); follows the active car via Retarget below.
		var drivePersist = hudGo.Components.Create<DriveModePersister>();
		drivePersist.Target = target;

		// Ramp-hitch flight recorder (debug instrument, inert unless vp_ramptrace is set).
		// Same always-on Component pattern; follows the active car via Retarget below.
		var rampTrace = hudGo.Components.Create<RampTraceRecorder>();
		rampTrace.Target = target;

		var tuning = hudGo.Components.Create<TuningPanel>();
		tuning.Car = target;

		// Rename-a-saved-tune modal. Deliberately its OWN panel (not part of TuningPanel) so its text
		// field survives TuningPanel's every-frame rebuild — see TuneRenameState. No per-car Target: it
		// reads static rename state, so Retarget leaves it alone.
		hudGo.Components.Create<TuneRenameOverlay>();

		// Dial-explainer overlay (the "?" button on the Tuning header opens it). Also its OWN panel, for the
		// same reason as the rename modal: TuningPanel rebuilds every frame off live load, which would tear
		// down an interactive overlay placed inside it. Static content (reads only UiState.TuneHelpOpen), so
		// Retarget leaves it alone.
		hudGo.Components.Create<TuneHelpOverlay>();

		var session = hudGo.Components.Create<SessionMenu>();
		session.Target = target;

		// Public-UX panels. WorldControls (M) reads the live world/terrain state and drives the
		// bootstrap's real rebuild path; HelpOverlay (I) is static content that auto-shows once on a
		// fresh clone. Neither needs a per-car Target, so Retarget leaves them alone.
		// World switching is feature-gated off for now (players stay on Town) — only mount the panel
		// when the gate is on, so the M hotkey and panel are fully absent until the Stunt Track returns.
		if ( GameBootstrap.WorldSwitchEnabled )
			hudGo.Components.Create<WorldControls>();
		hudGo.Components.Create<HelpOverlay>();
	}

	/// <summary>
	/// Re-point every mounted HUD panel at a new controller after a live car swap
	/// (<see cref="CarSwitcher"/>). Same panel roster as <see cref="Mount"/>, so the HUD/telemetry/
	/// tuning/session all follow the car under control instead of a destroyed object.
	/// </summary>
	public static void Retarget( Scene scene, VehicleController target )
	{
		if ( scene is null || !target.IsValid() )
			return;

		foreach ( var hud in scene.GetAllComponents<VehicleHud>() )
			hud.Target = target;
		foreach ( var tel in scene.GetAllComponents<TelemetryPanel>() )
			tel.Target = target;
		foreach ( var pt in scene.GetAllComponents<PlayerTiming>() )
			pt.Target = target;
		foreach ( var dp in scene.GetAllComponents<DriveModePersister>() )
			dp.Target = target;
		foreach ( var rt in scene.GetAllComponents<RampTraceRecorder>() )
			rt.Target = target;
		foreach ( var tun in scene.GetAllComponents<TuningPanel>() )
			tun.Car = target;
		foreach ( var ses in scene.GetAllComponents<SessionMenu>() )
			ses.Target = target;
	}
}
