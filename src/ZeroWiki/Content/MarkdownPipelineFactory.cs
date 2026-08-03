using Markdig;

namespace ZeroWiki.Content;

/// <summary>
/// Builds the single <see cref="MarkdownPipeline"/> every Markdown operation in ZeroWiki shares —
/// frontmatter block extraction here (3.2), body-to-HTML rendering in block 3b. Configured once and
/// registered as a singleton (<see cref="ContentStorageStartupExtensions"/>) so a wrong default cannot
/// ship silently in the section that doesn't render: D13's raw-HTML disabling is already in force even
/// though nothing renders a page body until 3b.
/// </summary>
public static class MarkdownPipelineFactory
{
    public static MarkdownPipeline Create() =>
        new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .DisableHtml()
            .Build();
}
