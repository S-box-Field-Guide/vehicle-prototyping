namespace VehicleProto;

/// <summary>
/// The PUBLISH STAMP: a monotonically increasing build number
/// bumped on EVERY publish so a tester can always say exactly which build they are running. vehicle_prototyping
/// is SINGLE-PLAYER, so there is NO lobby metadata, NO StampLobby,
/// and NO client-side compatibility gate here: the stamp's only job is tester ATTRIBUTION (e.g. Kenny reporting
/// "I'm on build N" against a bug). It is surfaced in the boot log and on the Help overlay, nowhere on the wire.
///
/// RUN <c>tools/bump_publish_stamp.py</c> BEFORE EVERY PUBLISH — it increments <see cref="PublishStamp"/> and
/// dates <see cref="PublishStampNote"/>, then record the build in CHANGELOG.md and republish.
/// </summary>
public static class VpBuild
{
	/// <summary>Monotonically increasing publish counter. Bumped by tools/bump_publish_stamp.py before each
	/// publish. Starts at 1 (the first published build, which predated the stamp).</summary>
	public const int PublishStamp = 9;

	/// <summary>Human note for the current stamp (date + gist). Updated alongside the bump. Keep the
	/// --note gist SHORT — this renders verbatim in the Help overlay footer (design-locked panel);
	/// the full story of each build lives in CHANGELOG.md.</summary>
	public const string PublishStampNote = "2026-07-17 - town polish, analog triggers, UI improvements (build 9)";
}
