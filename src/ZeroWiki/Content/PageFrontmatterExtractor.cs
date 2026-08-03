using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace ZeroWiki.Content;

/// <summary>
/// Extracts a page's optional YAML frontmatter block from its raw Markdown content and parses it into a
/// <see cref="PageFrontmatter"/> (3.2). Markdig's frontmatter extension only delimits the block — it
/// hands over <c>yamlBlock.Lines.ToString()</c> and evaluates nothing (D14) — so this class's only job is
/// finding that block and handing its text to <see cref="IFrontmatterParser"/>; it never inspects YAML
/// syntax itself.
/// </summary>
public sealed class PageFrontmatterExtractor
{
    private readonly MarkdownPipeline _pipeline;
    private readonly IFrontmatterParser _frontmatterParser;

    public PageFrontmatterExtractor(MarkdownPipeline pipeline, IFrontmatterParser frontmatterParser)
    {
        _pipeline = pipeline;
        _frontmatterParser = frontmatterParser;
    }

    /// <summary>
    /// Returns <see cref="PageFrontmatter.Empty"/> when <paramref name="markdownContent"/> has no
    /// frontmatter block — indistinguishable, by design (D14), from a block that failed to parse.
    /// </summary>
    public PageFrontmatter Extract(string markdownContent)
    {
        var document = Markdown.Parse(markdownContent, _pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        return yamlBlock is null
            ? PageFrontmatter.Empty
            : _frontmatterParser.Parse(yamlBlock.Lines.ToString());
    }
}
