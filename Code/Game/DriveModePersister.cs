namespace VehicleProto;

/// <summary>
/// LIVE-UNVERIFIED (2026-07-21). Watches the active car's drive mode (<see cref="VehicleController.Assists"/>)
/// and mirrors any change into the persisted player preference (<see cref="UserSettings.AssistLevel"/>),
/// so whichever mode the player leaves the session on (Casual/Sport/Sim) is what greets them next time
/// (owner: "I want the drive mode you set to save between sessions. If I select Sport, next time I go
/// in it should stay on Sport"). ONE global preference, not per-car.
///
/// Polling, not an event, because the two paths that actually CHANGE Assists live in different places
/// and one of them is out of scope to hook directly: the SessionMenu segbar writes straight onto
/// Target.Assists (<c>SessionMenu.SetAssist</c>), and the kit's own in-drive D-pad-up/B cycle
/// (<c>VehicleController.CycleDriveMode</c>, Libraries/fieldguide.vehiclephysics — vendored kit code,
/// not to be edited) ALSO writes straight onto Assists. Both converge on the same property, so one
/// poll site here catches both without touching kit code — the same trick <see cref="VehicleHud"/>
/// already uses to detect a drive-mode change for its press-flash.
///
/// Seed-on-first-frame guard (mirrors VehicleHud's _flashSeeded): a car retarget (Tab switch) never
/// counts as a change and re-saves — <see cref="CarSwitcher"/> already carries the LIVE Assists value
/// across a car swap, so the incoming car's Assists equals what was just seen here, not the incoming
/// def's DefaultAssists.
/// </summary>
public sealed class DriveModePersister : Component
{
	public VehicleController Target { get; set; }

	AssistLevel _last;
	bool _seeded;

	protected override void OnUpdate()
	{
		var assist = Target?.Assists ?? AssistLevel.Casual;

		if ( !_seeded )
		{
			_last = assist;
			_seeded = true;
			return;
		}

		if ( assist == _last )
			return;

		_last = assist;
		UserSettings.AssistLevel = assist;
	}
}
