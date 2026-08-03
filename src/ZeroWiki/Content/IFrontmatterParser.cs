namespace ZeroWiki.Content;

/// <summary>
/// Parses a page's raw YAML frontmatter block text into a <see cref="PageFrontmatter"/> (D14). This is
/// the only seam through which a YAML library is reached — the block's delimiting is Markdig's job
/// (<see cref="PageFrontmatterExtractor"/>), evaluating it is this interface's.
/// </summary>
/// <remarks>
/// <b>Failure is total, never partial</b> (D14): on any input that does not parse, exceeds the
/// implementation's bounds, or is otherwise untrustworthy, an implementation MUST return
/// <see cref="PageFrontmatter.Empty"/> — never a value built from whatever the parser managed to read
/// before it failed. Partially-parsed metadata is indistinguishable from real metadata, and would
/// silently give a page the wrong tags rather than none.
/// </remarks>
public interface IFrontmatterParser
{
    /// <summary>
    /// Parses <paramref name="yaml"/> — the frontmatter block's raw text, with no <c>---</c> delimiters —
    /// into a <see cref="PageFrontmatter"/>. Never throws; any fault yields <see cref="PageFrontmatter.Empty"/>.
    /// </summary>
    PageFrontmatter Parse(string yaml);
}
