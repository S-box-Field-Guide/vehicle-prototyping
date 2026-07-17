namespace VehicleProto;

/// <summary>
/// Global HUD modal/overlay flags (ui-razor.md modal-routing pattern). One bool per
/// toggleable panel; each toggle KEY is owned by exactly one component's OnUpdate to
/// avoid same-frame open/close races. Read by any panel that must react to another's
/// state (e.g. VehicleHud hides its hint bar while the telemetry overlay is up).
/// </summary>
public static class UiState
{
	/// <summary>L — instrumented telemetry overlay (TelemetryPanel owns the toggle, "Telemetry"
	/// action). Rides a letter key: the published client captures F-keys (old F4 bind was dead
	/// outside the editor).</summary>
	public static bool TelemetryOpen;

	/// <summary>T — lab / tuning panel (TuningPanel owns the toggle, via "Debug" action).</summary>
	public static bool TuningOpen;

	/// <summary>"?" button on the Tuning panel header — the dial-explainer overlay (TuneHelpOverlay renders
	/// it; TuningPanel's "?" button owns the toggle). Click-only, no keybind. Lives in a SEPARATE panel from
	/// TuningPanel so its BuildHash stays constant while open (TuningPanel rebuilds every frame off live
	/// suspension load — same reason as TuneRenameOverlay). Only meaningful while <see cref="TuningOpen"/>.</summary>
	public static bool TuneHelpOpen;

	/// <summary>Tab — session menu (SessionMenu owns the toggle, via "Session" action).</summary>
	public static bool SessionOpen;

	/// <summary>M — world &amp; terrain control panel (WorldControls owns the toggle, "World" action).
	/// F2 is stolen by the s&box host, so this rides a letter key.</summary>
	public static bool WorldOpen;

	/// <summary>I — first-run controls/help overlay (HelpOverlay owns the toggle, "Help" action).
	/// Auto-shows once on a fresh clone (file-backed flag), reopenable any time. F1/Esc are host-stolen.</summary>
	public static bool HelpOpen;

	/// <summary>H — hide the drive HUD chrome for clean screenshots / free view
	/// (VehicleHud owns the toggle, "HideHud" action). Overlay panels (I/M/T/Tab/L) still work.</summary>
	public static bool HudHidden;

	/// <summary>Any cursor-grabbing overlay is up (Tuning / Session / World / Help unlock the mouse).</summary>
	public static bool AnyCursorModalOpen => TuningOpen || SessionOpen || WorldOpen || HelpOpen;

	/// <summary>Reset on scene boot — statics survive Play→Stop→Play in the editor.</summary>
	public static void Reset()
	{
		TelemetryOpen = false;
		TuningOpen = false;
		TuneHelpOpen = false;
		SessionOpen = false;
		WorldOpen = false;
		HelpOpen = false;
		HudHidden = false;
	}
}
