using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="MarkdownLinkAllowList"/> as a class of destinations, not a table of examples
/// (S1, block 3 remediation) — the defect this closes survived three prior audits because every one of
/// them asked "which values reach the browser un-encoded", a question that only ever inspects tags, and
/// every test asserted on <c>&lt;script&gt;</c> alone. These tests assert on rendered attributes instead,
/// and include the entity-encoded, mixed-case, and whitespace-padded forms the brief specifically named.
/// </summary>
public sealed class MarkdownLinkAllowListTests
{
    private static readonly MarkdownPipeline Pipeline = MarkdownPipelineFactory.Create();

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/x?y=1")]
    [InlineData("mailto:me@example.com")]
    [InlineData("mailto:")]
    [InlineData("#section")]
    [InlineData("sub/page")]
    [InlineData("../other")]
    [InlineData("/wiki/other-page")]
    [InlineData("/wiki/other-page#section")]
    [InlineData("")]
    public void IsAllowedDestination_AllowsRelativeAndExplicitlyPermittedSchemes(string destination) =>
        Assert.True(MarkdownLinkAllowList.IsAllowedDestination(destination));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")] // case
    [InlineData("  javascript:alert(1)")] // leading whitespace
    [InlineData("javascript:alert(1)  ")] // trailing whitespace
    [InlineData("java\tscript:alert(1)")] // embedded tab -- a real browser strips this, then sees javascript:
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("note:something")] // an unrecognised scheme -- refused per "including schemes nobody has thought of"
    public void IsAllowedDestination_RefusesScriptBearingAndUnknownSchemes(string destination) =>
        Assert.False(MarkdownLinkAllowList.IsAllowedDestination(destination));

    /// <summary>
    /// A regression this exact allow-list would have introduced: <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>
    /// with <see cref="UriKind.Absolute"/> treats a root-relative string as an absolute <c>file:</c> URI
    /// (confirmed empirically), which is why the implementation does not use <see cref="Uri"/> for
    /// classification at all. If this regresses, ordinary relative wiki links stop working.
    /// </summary>
    [Fact]
    public void IsAllowedDestination_DoesNotMisclassifyARootRelativePathAsAnAbsoluteFileUri()
    {
        Assert.True(Uri.TryCreate("/wiki/other-page", UriKind.Absolute, out var asUri));
        Assert.Equal("file", asUri.Scheme);

        Assert.True(MarkdownLinkAllowList.IsAllowedDestination("/wiki/other-page"));
    }

    [Theory]
    [InlineData("sub/\x01page")] // SOH - an otherwise-relative destination, refused on the class alone
    [InlineData("sub/\x7fpage")] // DEL
    public void IsAllowedDestination_RefusesAnEmbeddedControlCharacterOtherThanTabCrLf(string destination) =>
        Assert.False(MarkdownLinkAllowList.IsAllowedDestination(destination));

    [Theory]
    [InlineData("[click me](javascript:alert(1))")]
    [InlineData("[click me](JavaScript:alert(1))")]
    [InlineData("[click me]( javascript:alert(1) )")]
    [InlineData("[click me](javascript&#58;alert(1))")] // entity-encoded colon
    public void Enforce_NeutralizesTheHrefAttributeOfAScriptBearingLink(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"\"", html, StringComparison.Ordinal);
        // The visible label survives -- only the destination is neutralized.
        Assert.Contains("click me", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_NeutralizesTheSrcAttributeOfAScriptBearingImage()
    {
        var html = Markdig.Markdown.ToHtml("![img](javascript:alert(1))", Pipeline);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src=\"\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_NeutralizesAScriptBearingAutolink()
    {
        var html = Markdig.Markdown.ToHtml("<javascript:alert(1)>", Pipeline);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enforce_LeavesOrdinaryDestinationsUnchanged()
    {
        var html = Markdig.Markdown.ToHtml("[ok](http://example.com) and [wiki](/wiki/other)", Pipeline);

        Assert.Contains("href=\"http://example.com\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/wiki/other\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_OperatesOnTheParsedDocumentDirectly()
    {
        var document = Markdig.Markdown.Parse("[click me](javascript:alert(1))", Pipeline);
        var link = Assert.IsType<LinkInline>(document.Descendants<LinkInline>().Single());

        // MarkdownPipelineFactory wires Enforce through DocumentProcessed, so Parse alone already
        // neutralized this -- confirming no caller can render through this pipeline while skipping it.
        Assert.Equal(string.Empty, link.Url);
    }
}
