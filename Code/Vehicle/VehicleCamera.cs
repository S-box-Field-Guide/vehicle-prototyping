namespace VehicleProto;

/// <summary>
/// Chase camera (spec 5.2.4): spring-arm follow behind the car's flattened heading,
/// FOV widens with speed. Mouse orbits around the car; after a few seconds without
/// mouse input it eases back to the chase position. Mouse wheel zooms distance.
/// </summary>
public sealed class VehicleCamera : Component
{
	public VehicleController Target { get; set; }

	[Property] public float Distance { get; set; } = 6.5f; // m
	[Property] public float Height { get; set; } = 2.2f;   // m
	[Property] public float BaseFov { get; set; } = 70f;
	[Property] public float OrbitReturnDelay { get; set; } = 5f;
	[Property] public float MinDistance { get; set; } = 3.0f;  // m — wheel zoom in
	[Property] public float MaxDistance { get; set; } = 14.0f; // m — wheel zoom out
	[Property] public float ZoomPerNotch { get; set; } = 0.85f; // m per wheel tick

	CameraComponent _camera;
	float _orbitYaw;
	float _orbitPitch;
	TimeSince _lastMouseInput = 999f;

	protected override void OnStart()
	{
		_camera = Components.Get<CameraComponent>();
		// Drive mode needs a locked cursor or AnalogLook stays at zero. UI modals unlock
		// it; we re-lock every frame when none are open (panels only set Visible while up).
		if ( !UiState.AnyCursorModalOpen )
			Mouse.Visibility = MouseVisibility.Hidden;
	}

	protected override void OnUpdate()
	{
		if ( Target is null || !Target.IsValid() )
			return;

		bool uiOwnsCursor = UiState.AnyCursorModalOpen;
		if ( !uiOwnsCursor )
		{
			// Re-assert every frame — panel dismiss paths miss edge cases, and the engine
			// can resurface the cursor after focus changes.
			Mouse.Visibility = MouseVisibility.Hidden;

			var look = Input.AnalogLook;
			if ( MathF.Abs( look.yaw ) > 0.05f || MathF.Abs( look.pitch ) > 0.05f )
			{
				_lastMouseInput = 0;
				_orbitYaw += look.yaw;
				_orbitPitch = Math.Clamp( _orbitPitch + look.pitch, -30f, 12f );
			}

			// Scroll up = closer (smaller Distance). Ignore while UI has the cursor.
			float scroll = Input.MouseWheel.y;
			if ( MathF.Abs( scroll ) > 0.01f )
				Distance = Math.Clamp( Distance - scroll * ZoomPerNotch, MinDistance, MaxDistance );
		}

		if ( !uiOwnsCursor && _lastMouseInput > OrbitReturnDelay )
		{
			// ease back behind the car
			float decay = 1f - MathF.Exp( -3f * Time.Delta );
			_orbitYaw = MathX.Lerp( _orbitYaw, 0f, decay );
			_orbitPitch = MathX.Lerp( _orbitPitch, 0f, decay );
		}

		float m = Units.MetersToUnits;
		var car = Target.GameObject;

		// follow the flattened heading so rolls/flips don't whip the camera
		var flatForward = car.WorldRotation.Forward.WithZ( 0f );
		flatForward = flatForward.IsNearZeroLength ? Vector3.Forward : flatForward.Normal;
		float baseYaw = MathF.Atan2( flatForward.y, flatForward.x ).RadianToDegree();

		var orbit = Rotation.From( _orbitPitch, baseYaw + _orbitYaw, 0f );

		float speedStretch = Math.Clamp( Target.SpeedMs / 40f, 0f, 1f );
		var wantedPos = car.WorldPosition
			- orbit.Forward * (Distance + speedStretch * 1.5f) * m
			+ Vector3.Up * Height * m;

		float smooth = 1f - MathF.Exp( -8f * Time.Delta );
		WorldPosition = Vector3.Lerp( WorldPosition, wantedPos, smooth );

		var lookTarget = car.WorldPosition + Vector3.Up * 0.8f * m + orbit.Forward * 1.5f * m;
		WorldRotation = Rotation.LookAt( (lookTarget - WorldPosition).Normal );

		if ( _camera.IsValid() )
			_camera.FieldOfView = BaseFov + speedStretch * 15f;
	}
}
