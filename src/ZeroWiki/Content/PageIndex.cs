namespace ZeroWiki.Content;

/// <summary>
/// The process-memory holder for the wiki's current <see cref="PageIndexSnapshot"/> (D15). Nothing is
/// persisted — this singleton is the only place a snapshot exists between rebuilds, installed once at
/// startup and swapped wholesale on every later rebuild.
/// </summary>
/// <remarks>
/// This block (4.1–4.2) only builds and installs the initial snapshot at startup
/// (<see cref="ContentStorageStartupExtensions.BuildPageIndexAsync"/>). Comparing <see cref="Current"/>'s
/// <see cref="PageIndexSnapshot.CommitSha"/> against the repository's live <c>HEAD</c> and refreshing on a
/// mismatch is block B (4.3) — nothing here reads or checks the stamp.
/// </remarks>
public sealed class PageIndex
{
    private PageIndexSnapshot _current = PageIndexSnapshot.Empty;

    /// <summary>The most recently installed snapshot.</summary>
    public PageIndexSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically installs <paramref name="snapshot"/> as <see cref="Current"/>. A reference swap, not a
    /// mutation — a caller reading <see cref="Current"/> mid-rebuild sees either the old snapshot
    /// wholesale or the new one, never a torn mix of the two.
    /// </summary>
    public void Replace(PageIndexSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
