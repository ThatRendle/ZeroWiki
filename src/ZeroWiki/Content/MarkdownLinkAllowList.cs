using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ZeroWiki.Content;

/// <summary>
/// Refuses a link or image destination whose scheme is not explicitly allowed (D13's addendum, S1 —
/// block 3 remediation). <see cref="MarkdownPipelineFactory"/> subscribes <see cref="Enforce"/> to the
/// pipeline's <c>DocumentProcessed</c> event, so it runs on every parse this pipeline ever does — no
/// call site can forget to invoke it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>DisableHtml()</c> escapes raw HTML, but Markdig has no default
/// allow-list for link/image *destinations* — <c>[x](javascript:alert(1))</c> parses to a perfectly
/// ordinary <see cref="LinkInline"/> whose <see cref="LinkInline.Url"/> is the literal string
/// <c>javascript:alert(1)</c>, which the shipped renderer then wrote straight into <c>href</c>. Stored
/// XSS, reachable by any git-token holder, and the exact threat D13 names.
/// </para>
/// <para>
/// <b>Runs after Markdig has parsed and resolved entities, never against raw Markdown.</b> The entity
/// form <c>javascript&amp;#58;alert(1)</c> is not <c>javascript:</c> until Markdig decodes it during
/// parsing — checking <see cref="LinkInline.Url"/> (post-parse) rather than the source text is what
/// makes the entity-encoded bypass and the literal form indistinguishable to this check.
/// </para>
/// <para>
/// <b>Normalization matches what a browser's URL parser does, not just what looks safe.</b> A real
/// browser strips every embedded ASCII tab/CR/LF from a URL — not just at the ends — before
/// interpreting its scheme, so <c>java\tscript:alert(1)</c> is genuinely <c>javascript:</c> once
/// rendered. Checking the un-normalized string would be a bypass, not a refusal. Any *other* embedded
/// control character is refused outright rather than modeled further. Scheme comparison is
/// case-insensitive and does not use <see cref="Uri"/> for the classification itself — <see cref="Uri"/>
/// treats a root-relative string like <c>/wiki/other-page</c> as an absolute <c>file:</c> URI under
/// <see cref="UriKind.Absolute"/> (confirmed empirically), which would have wrongly refused every
/// ordinary relative link. Scheme extraction instead follows RFC 3986's own grammar directly:
/// <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"</c> at the start of the string.
/// </para>
/// <para>
/// <b>Refusal neutralizes rather than removes.</b> A disallowed destination's <see cref="LinkInline.Url"/>
/// (or <see cref="AutolinkInline.Url"/>) is set to the empty string — confirmed by rendering the mutated
/// document that this produces <c>href=""</c>/<c>src=""</c> rather than throwing or reintroducing the
/// original value some other way (e.g. via <see cref="LinkInline.GetDynamicUrl"/>, which this pipeline
/// never sets). The link or image text itself, and everything else on the page, is untouched.
/// </para>
/// </remarks>
public static class MarkdownLinkAllowList
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>Neutralizes every disallowed link, image, and autolink destination in <paramref name="document"/>, in place.</summary>
    public static void Enforce(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsAllowedDestination(link.Url))
            {
                link.Url = string.Empty;
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            if (!IsAllowedDestination(autolink.Url))
            {
                autolink.Url = string.Empty;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="destination"/> is relative (including a bare fragment) or carries an
    /// explicitly allowed scheme (<c>http</c>, <c>https</c>, <c>mailto</c>). Everything else — including
    /// a scheme nobody has thought of — is refused.
    /// </summary>
    public static bool IsAllowedDestination(string? destination)
    {
        if (string.IsNullOrEmpty(destination))
        {
            return true;
        }

        var normalized = RemoveEmbeddedTabCrLf(destination).Trim();
        if (normalized.Length == 0)
        {
            return true;
        }

        if (normalized.Any(char.IsControl))
        {
            return false;
        }

        if (normalized.StartsWith('#'))
        {
            return true;
        }

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex <= 0 || !IsValidSchemeToken(normalized.AsSpan(0, colonIndex)))
        {
            // No RFC 3986 scheme prefix at all -- a relative reference.
            return true;
        }

        var scheme = normalized[..colonIndex];
        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    private static string RemoveEmbeddedTabCrLf(string value)
    {
        if (!value.Any(c => c is '\t' or '\r' or '\n'))
        {
            return value;
        }

        return new string(value.Where(c => c is not ('\t' or '\r' or '\n')).ToArray());
    }

    /// <summary>RFC 3986: <c>scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>.</summary>
    private static bool IsValidSchemeToken(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }

        foreach (var c in candidate[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
