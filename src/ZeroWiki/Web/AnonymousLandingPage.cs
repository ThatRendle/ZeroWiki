using System.Text;

namespace ZeroWiki.Web;

/// <summary>
/// The single response an unauthenticated visitor gets from every non-exempt URL (AD21).
/// </summary>
/// <remarks>
/// <para>
/// It is a constant rather than a Razor component on purpose. The property AD21 buys is that a URL
/// which exists and is protected and one that does not exist at all are <em>byte-identical</em>, so
/// a stranger cannot map the site by probing names; a constant is that property by construction,
/// whereas anything rendered through the router carries the request's own URL into
/// <c>NavigationManager</c>, the <c>&lt;base&gt;</c> href and focus management, where one leak is
/// enough to undo it. Being URL-independent is also what makes the response cacheable at an edge.
/// </para>
/// <para>
/// The stylesheets are referenced at their un-fingerprinted paths because those do not move between
/// builds; <c>MapStaticAssets</c> serves an asset at both spellings.
/// </para>
/// </remarks>
public static class AnonymousLandingPage
{
    /// <summary>
    /// The markup, verbatim. The inline script writes <c>pathname + search</c> and never
    /// <c>location.href</c>: the value it produces reaches the login page as an ordinary query
    /// string, so it is attacker-controlled like any other, and <see cref="LocalUrl.IsLocal"/> on
    /// the login page — not this script — is the boundary that holds. With scripting off the link
    /// stays a bare <c>/login</c> and signing in lands on the home page: degraded, never broken.
    /// </summary>
    public const string Html =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>ZeroWiki</title>
            <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css" />
            <link rel="stylesheet" href="/app.css" />
            <link rel="icon" type="image/png" href="/favicon.png" />
        </head>
        <body>
            <main class="content px-4">
                <h1>ZeroWiki</h1>
                <p>This wiki is private. Sign in to read it.</p>
                <p><a id="sign-in" href="/login">Sign in</a></p>
            </main>
            <script>
                (function () {
                    var target = location.pathname + location.search;
                    if (target === '/') { return; }
                    document.getElementById('sign-in').href = '/login?returnUrl=' + encodeURIComponent(target);
                })();
            </script>
        </body>
        </html>
        """;

    private const string ContentType = "text/html; charset=utf-8";

    private static readonly byte[] Utf8Html = Encoding.UTF8.GetBytes(Html);

    /// <summary>Writes the page as the whole response.</summary>
    /// <remarks>
    /// <para>
    /// The status is <c>200</c> deliberately. <c>401</c> is largely uncacheable at an edge and is
    /// the code §8's git Smart HTTP needs for real, with a <c>WWW-Authenticate</c> challenge — the
    /// web UI must not squat on it. <c>404</c> would move the existence oracle from the body into
    /// the status line, which is the whole thing AD21 closes.
    /// </para>
    /// <para>
    /// The length is set explicitly so two responses to different URLs cannot differ in their
    /// framing headers, and no <c>Cache-Control</c> is emitted: caching policy is a deployment
    /// concern whose safety property (bypass the cache when the authentication cookie is present)
    /// lives at the edge, and an app that says nothing cannot be the cause of a cached page
    /// reaching the wrong visitor.
    /// </para>
    /// </remarks>
    public static Task WriteAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        context.Response.ContentLength = Utf8Html.Length;

        return context.Response.Body.WriteAsync(Utf8Html, context.RequestAborted).AsTask();
    }
}
