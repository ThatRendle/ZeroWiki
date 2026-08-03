namespace ZeroWiki.Content;

/// <summary>
/// An immutable, point-in-time view of the wiki's derived index (D6, D15): every servable page, D12's
/// refusals (ambiguous routes and unreadable directories) riding in the <i>same</i> snapshot rather than
/// an index of servable pages only, and the commit this snapshot was built from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommitSha"/> is <see langword="null"/> exactly when the repository has no commits yet (an
/// unborn <c>HEAD</c>) — D15 treats that as "no pages", not a build failure, and <see cref="Empty"/> is
/// the snapshot for it.
/// </para>
/// <para>
/// <b>Metadata only</b> (D15): a page's body is always read from the working tree on the request that
/// renders it, never served from this snapshot, so stale metadata can result from an out-of-date
/// <see cref="CommitSha"/> but stale <i>content</i> never can. Nothing in this snapshot type — nor
/// anything that builds one — compares <see cref="CommitSha"/> against the repository's current
/// <c>HEAD</c>; that comparison, and the incremental re-index it drives, is block B (4.3).
/// </para>
/// </remarks>
public sealed record PageIndexSnapshot(
    IReadOnlyList<PageIndexEntry> Pages,
    IReadOnlyList<AmbiguousPageRoute> AmbiguousRoutes,
    IReadOnlyList<string> UnreadableDirectories,
    string? CommitSha)
{
    /// <summary>
    /// The snapshot for a repository with no commits yet (an unborn <c>HEAD</c>) — D15's "no pages",
    /// not a failure.
    /// </summary>
    public static readonly PageIndexSnapshot Empty = new([], [], [], null);
}
