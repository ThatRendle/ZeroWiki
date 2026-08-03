namespace ZeroWiki.Content;

/// <summary>
/// One page's metadata in a <see cref="PageIndexSnapshot"/> — everything <see cref="EnumeratedPage"/>
/// already establishes (D12: <see cref="Route"/> identifies exactly this file) plus what building the
/// index adds: frontmatter title/tags (D14) and the git-derived last-edit (D5, D15).
/// </summary>
/// <remarks>
/// <see cref="Title"/> is exactly the frontmatter title, nullable when there is none — no H1 extraction,
/// no filename fallback baked in here. That is a presentation choice belonging to whatever renders this
/// entry (<c>WikiPage</c>'s existing display fallback), not to the index.
/// </remarks>
public sealed record PageIndexEntry(
    string Route,
    string RelativePath,
    string AbsolutePath,
    string? Title,
    IReadOnlyList<string> Tags,
    PageLastEdit? LastEdit);
