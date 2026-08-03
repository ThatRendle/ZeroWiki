using Markdig;

namespace ZeroWiki.Content;

/// <summary>
/// Builds the single <see cref="MarkdownPipeline"/> every Markdown operation in ZeroWiki shares —
/// frontmatter block extraction here (3.2), body-to-HTML rendering in block 3b. Configured once and
/// registered as a singleton (<see cref="ContentStorageStartupExtensions"/>) so a wrong default cannot
/// ship silently in the section that doesn't render: D13's raw-HTML disabling and the link/image
/// destination allow-list (<see cref="MarkdownLinkAllowList"/>, S1 — block 3 remediation) are both
/// already in force even though nothing renders a page body until 3b.
/// </summary>
/// <remarks>
/// The allow-list is wired through <c>DocumentProcessed</c> rather than called explicitly at each render
/// site: it runs on every <c>Markdown.Parse</c>/<c>Markdown.ToHtml</c> call that shares this pipeline, so
/// a future caller cannot render through it while forgetting to enforce D13 — the exact failure shape
/// that let the destination-allow-list gap in this pipeline ship unnoticed through three prior audits.
/// </remarks>
public static class MarkdownPipelineFactory
{
    public static MarkdownPipeline Create()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .DisableHtml();

        builder.DocumentProcessed += MarkdownLinkAllowList.Enforce;

        return builder.Build();
    }
}
