using System.Diagnostics.CodeAnalysis;

namespace ZeroWiki.Web;

/// <summary>
/// Guards redirect targets that arrive from the request.
/// </summary>
/// <remarks>
/// A login page that redirects wherever a query string tells it to is an open redirect, and an
/// open redirect on a login page is a credential-phishing primitive: the attacker sends a link
/// to the real site, the visitor authenticates against the real form, and the redirect lands
/// them somewhere else entirely with the site's own domain in their history.
/// </remarks>
public static class LocalUrl
{
    /// <summary>
    /// Whether <paramref name="url"/> is a path on this site and cannot be read as pointing
    /// anywhere else.
    /// </summary>
    /// <remarks>
    /// Accepts only a single leading <c>/</c> followed by something that is not another slash or
    /// a backslash. That rules out absolute URLs (<c>https://evil.example</c>), protocol-relative
    /// URLs (<c>//evil.example</c>, which a browser resolves against the current scheme), the
    /// backslash variants browsers normalise into them (<c>/\evil.example</c>), and bare relative
    /// paths, which resolve against the current directory rather than the site root.
    /// </remarks>
    public static bool IsLocal([NotNullWhen(true)] string? url) =>
        url is ['/', var second, ..] ? second is not ('/' or '\\') : url == "/";
}
