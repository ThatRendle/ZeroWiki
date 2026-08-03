namespace ZeroWiki.Content;

/// <summary>
/// The result of walking <see cref="ContentPaths.WorkingTree"/> once (<see cref="PageEnumerationService"/>):
/// every page whose route identifies it and only it (D12), every route that fails that invariant, and
/// every subdirectory that could not be read at all. All three lists are independent — a page under a
/// failing or unreadable subtree never appears in <see cref="Pages"/>, but every page elsewhere in the
/// tree is unaffected by either.
/// </summary>
public sealed record PageEnumerationResult(
    IReadOnlyList<EnumeratedPage> Pages,
    IReadOnlyList<AmbiguousPageRoute> AmbiguousRoutes,
    IReadOnlyList<string> UnreadableDirectories);
