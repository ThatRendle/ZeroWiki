using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageFrontmatterExtractor"/> against the shared <see cref="MarkdownPipelineFactory"/>
/// pipeline and the real <see cref="SharpYamlFrontmatterParser"/> — the scenarios named in
/// <c>specs/content-store/spec.md</c>: a page with no frontmatter block, and one whose block fails to
/// parse, both degrade to empty metadata rather than failing the page.
/// </summary>
public sealed class PageFrontmatterExtractorTests
{
    private readonly PageFrontmatterExtractor _extractor =
        new(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser());

    [Fact]
    public void PageWithoutFrontmatter_IsRenderedWithEmptyMetadata()
    {
        var result = _extractor.Extract("# Just a heading\n\nNo frontmatter here.\n");

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void PageWithValidFrontmatter_ExtractsTitleAndTags()
    {
        var markdown = "---\ntitle: Kick Off\ntags: [meeting, project-x]\n---\n\n# Body\n\nContent.\n";

        var result = _extractor.Extract(markdown);

        Assert.Equal("Kick Off", result.Title);
        Assert.Equal(["meeting", "project-x"], result.Tags);
    }

    [Fact]
    public void PageWithMalformedFrontmatter_DegradesToEmptyMetadataRatherThanFailing()
    {
        var markdown = "---\ntitle: [unterminated\n---\n\n# Body\n\nStill has content.\n";

        var result = _extractor.Extract(markdown);

        Assert.Equal(PageFrontmatter.Empty, result);
    }

    [Fact]
    public void EmptyFrontmatterBlock_IsEmptyMetadata()
    {
        var markdown = "---\n---\n\n# Body\n";

        var result = _extractor.Extract(markdown);

        Assert.Equal(PageFrontmatter.Empty, result);
    }
}
