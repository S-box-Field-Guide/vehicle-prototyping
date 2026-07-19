namespace FieldGuide.VehiclePhysics;

/// <summary>
/// Per-frame driver intent, the value-struct swap point at VehicleController's input seam
/// (<see cref="VehicleController.InputOverride"/>). Carries the raw intent the controller samples
/// from a device: the move axis (forward/back + steer), the handbrake action, and the sequential
/// shift requests. A keyboard/gamepad/wheel source or a scripted source (a test pilot, an AI) all
/// produce one of these, so the controller's gear/reverse/steer-ramp logic stays source-agnostic.
///
/// The live-device sampler <see cref="SampleDeviceInputs"/> and its gamepad shaping helpers live
/// here (lifted out of the controller): the controller either consumes an injected override or calls
/// this sampler, and nothing else in the class touches device input.
/// </summary>
public struct DriveInputs
{
	/// <summary>-1..1 signed drive axis: +forward accelerates, -back brakes then engages reverse near a
	/// stop. Built in <see cref="SampleDeviceInputs"/> as (throttle − brake) where each channel is the
	/// MAX of the keyboard/stick component (<c>Input.AnalogMove.x</c>) and the ANALOG gamepad trigger
	/// pull (<c>Input.GetAnalog(InputAnalog.RightTrigger|LeftTrigger)</c>, 0..1) — so a partial trigger
	/// gives a partial pedal. A scripted source sets this float directly.</summary>
	public float MoveForward;

	/// <summary>-1..1, maps to <c>Input.AnalogMove.y</c> (gamepad path reshaped by the gamepad deadzone
	/// + response curve). Note <c>Rotation.FromYaw(+)</c> is a LEFT/CCW turn, so +Steer steers left.</summary>
	public float Steer;

	/// <summary>The handbrake / drift button: keyboard "Jump" (Space), or gamepad A (Jump's own
	/// GamepadCode) or the left bumper ("Handbrake" action).</summary>
	public bool Handbrake;

	/// <summary>Edge-triggered request to shift UP one gear (sequential MANUAL mode). Keyboard E /
	/// gamepad R1 while live; a scripted source pulses it for one tick. The controller rising-edge-
	/// detects it, so a source that holds it across ticks still shifts exactly once.</summary>
	public bool ShiftUp;

	/// <summary>Edge-triggered request to shift DOWN one gear (sequential MANUAL mode). Keyboard Q /
	/// gamepad L1. Same one-shot rising-edge semantics as <see cref="ShiftUp"/>.</summary>
	public bool ShiftDown;

	/// <summary>Edge-triggered request to toggle the transmission mode AUTO↔MANUAL. Keyboard G /
	/// gamepad D-pad down. Same one-shot rising-edge semantics as <see cref="ShiftUp"/>.</summary>
	public bool ShiftModeToggle;

	// Gamepad tier: deadzone + response curve for the analog steer axis.
	const float GamepadSteerDeadzone = 0.12f;
	const float GamepadSteerCurvePower = 1.6f; // >1 softens the center for fine control, still reaches full lock

	// Analog throttle/brake tier: a small trigger deadzone so resting triggers can't creep the pedals.
	// The engine ALSO applies its own 12.5% deadzone one layer down (Controller.SetAxis zeroes any
	// |axis| <= 0.125 before Input.GetAnalog sees it), so values arrive as 0 or >~0.125 and this floor
	// is a belt-and-suspenders guard that also rescales so full pull still reaches 1.0.
	const float GamepadTriggerDeadzone = 0.05f;

	/// <summary>Sample the live input devices into a DriveInputs value (this is the keyboard/gamepad
	/// source; other sources produce the same struct and set <see cref="VehicleController.InputOverride"/>).
	///
	/// Steering rides <c>Input.AnalogMove.y</c> straight off the left stick; keyboard emits exact
	/// -1/0/1 through the same path and passes through <see cref="ApplyGamepadSteerCurve"/> unchanged.
	///
	/// Throttle/brake are VARIABLE per device: keyboard W/S ride <c>Input.AnalogMove.x</c> as an exact
	/// ±1 digital forward/back; the gamepad triggers are read as a true ANALOG 0..1 pull via
	/// <c>Input.GetAnalog(InputAnalog.RightTrigger|LeftTrigger)</c> (right = gas, left = brake). The two
	/// devices combine per channel by MAX — so either device works and neither fights the other — then
	/// the net (throttle − brake) folds into the single signed <see cref="MoveForward"/> scalar. On
	/// keyboard <c>Input.GetAnalog</c> returns 0, so keyboard-only players are byte-identical.</summary>
	public static DriveInputs SampleDeviceInputs()
	{
		var move = Input.AnalogMove;

		// keyboard/stick forward+back split off the shared move axis (W = +x, S = -x)
		float keyThrottle = MathF.Max( 0f, move.x );
		float keyBrake = MathF.Max( 0f, -move.x );

		// gamepad triggers, true analog 0..1 (right = gas, left = brake)
		float triggerThrottle = ReadTrigger( InputAnalog.RightTrigger );
		float triggerBrake = ReadTrigger( InputAnalog.LeftTrigger );

		// MAX blend per channel so either device drives the pedal, neither overrides the other
		float throttle = MathF.Max( keyThrottle, triggerThrottle );
		float brake = MathF.Max( keyBrake, triggerBrake );
		float moveForward = Math.Clamp( throttle - brake, -1f, 1f );

		return new DriveInputs
		{
			MoveForward = moveForward,
			Steer = ApplyGamepadSteerCurve( move.y ),
			Handbrake = Input.Down( "Jump" ) || Input.Down( "Handbrake" ),
		};
	}

	/// <summary>Read a gamepad trigger as a linear 0..1 pull with a small deadzone floor (rescaled so
	/// full pull still reaches 1.0). <c>Input.GetAnalog</c> already returns 0 for a trigger on keyboard,
	/// so this only shapes the gamepad path.</summary>
	static float ReadTrigger( InputAnalog trigger )
	{
		float v = Math.Clamp( Input.GetAnalog( trigger ), 0f, 1f );
		if ( v < GamepadTriggerDeadzone )
			return 0f;
		return (v - GamepadTriggerDeadzone) / (1f - GamepadTriggerDeadzone);
	}

	/// <summary>Deadzone + power curve for the analog steer axis. Values under the deadzone snap to 0;
	/// the remaining range is rescaled so full stick deflection still reaches ±1 (no lost lock), then
	/// raised to <see cref="GamepadSteerCurvePower"/> for a softer center. Keyboard's exact -1/0/1
	/// passes through unaffected (0 is inside the deadzone; 1 rescales to 1 and 1^n == 1).</summary>
	static float ApplyGamepadSteerCurve( float raw )
	{
		float mag = MathF.Abs( raw );
		if ( mag < GamepadSteerDeadzone )
			return 0f;

		float t = Math.Clamp( (mag - GamepadSteerDeadzone) / (1f - GamepadSteerDeadzone), 0f, 1f );
		return MathF.Sign( raw ) * MathF.Pow( t, GamepadSteerCurvePower );
	}
}
