namespace VehicleProto;

/// <summary>
/// Cross-panel handshake for renaming a saved tune, and the reason renaming a preset is even possible.
/// <para>
/// THE PROBLEM: <see cref="TuningPanel"/> folds live suspension load into its <c>BuildHash</c>, so it
/// rebuilds EVERY FRAME while driving. A razor <c>TextEntry</c> placed inside it is destroyed and
/// recreated on every rebuild, losing focus each frame — typing is impossible. This is why the presets
/// MVP shipped auto-names ("&lt;car&gt; tune N") instead of a rename field.
/// </para>
/// <para>
/// THE FIX: the rename text field lives in a SEPARATE panel — <see cref="TuneRenameOverlay"/> — whose
/// <c>BuildHash</c> reads ONLY <see cref="Active"/> and <see cref="Revision"/> (both change only on
/// begin/cancel/commit, never per frame). While the player types, that panel's hash is CONSTANT, so it
/// never rebuilds, so its <c>TextEntry</c> object is created ONCE and never torn down mid-edit — focus
/// and caret survive by construction. TuningPanel keeps rebuilding harmlessly behind the modal; the
/// text field is fully isolated from it.
/// </para>
/// One rename at a time (single-player, one panel stack), so static state is unambiguous.
/// </summary>
public static class TuneRenameState
{
	/// <summary>The preset currently being renamed, or null when no rename is in progress. The live
	/// stored instance (TunePresetStore hands out live references), so a commit mutates it in place.</summary>
	public static TunePreset Target { get; private set; }

	/// <summary>True while a rename modal should be shown.</summary>
	public static bool Active => Target is not null;

	/// <summary>Bumped on every begin/cancel/commit. The overlay hashes this so it rebuilds on those
	/// transitions (open/close) but NOT while the player types; TuningPanel also hashes it so a committed
	/// rename re-renders the list even when the car is sitting still (load isn't changing the hash).</summary>
	public static int Revision { get; private set; }

	/// <summary>Open the rename modal for <paramref name="preset"/>.</summary>
	public static void Begin( TunePreset preset )
	{
		if ( preset is null )
			return;
		Target = preset;
		Revision++;
	}

	/// <summary>Close the modal without changing the name.</summary>
	public static void Cancel()
	{
		if ( Target is null )
			return;
		Target = null;
		Revision++;
	}

	/// <summary>Apply <paramref name="newName"/> to the target through the store (which rejects
	/// empty/whitespace and enforces per-car uniqueness) and close the modal. Returns true if the stored
	/// name actually changed.</summary>
	public static bool Commit( string newName )
	{
		if ( Target is null )
			return false;
		bool changed = TunePresetStore.Rename( Target, newName );
		Target = null;
		Revision++;
		return changed;
	}

	/// <summary>Hard-reset for a fresh session — statics survive Play→Stop→Play in the editor, so a
	/// session torn down mid-rename must not leak an open modal into the next play.</summary>
	public static void Reset()
	{
		Target = null;
		Revision++;
	}
}
