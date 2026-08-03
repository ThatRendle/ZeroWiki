namespace ZeroWiki.Content;

/// <summary>
/// A page's parsed frontmatter (D14). Deliberately a fixed shape with only the fields the wiki actually
/// reads — never a raw, attacker-shaped dictionary passed through to a caller — so nothing downstream can
/// be surprised by an unexpected type or an unbounded structure surviving the parse.
/// </summary>
public sealed record PageFrontmatter
{
    /// <summary>
    /// The value every implementation of <see cref="IFrontmatterParser"/> MUST return for a missing or
    /// unparseable frontmatter block: empty, never partially populated.
    /// </summary>
    public static readonly PageFrontmatter Empty = new();

    public string? Title { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
}
