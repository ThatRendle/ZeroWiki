using System.Net;
using System.Text.RegularExpressions;
using ZeroWiki.Web;

namespace ZeroWiki.Tests.Web;

public static partial class HttpAssertions
{
    /// <summary>
    /// Strips the Blazor Server persisted-component-state marker
    /// (<c>&lt;!--Blazor-Server-Component-State:...--&gt;</c>) that every Razor Components response now
    /// carries once §8 block B wires <c>InteractiveServer</c> into the app (D7, D19 §3) — appended after
    /// <c>&lt;/html&gt;</c> regardless of whether the specific page renders any interactive component,
    /// since the render mode is an app-wide capability rather than a per-page one. It is DataProtection-
    /// encrypted, so it differs between any two responses even when their visible content is otherwise
    /// identical. A test comparing two responses for a uniform-response property (AD17, AD21) must
    /// normalise this away first, exactly as an antiforgery token already needs normalising.
    /// </summary>
    public static string StripPersistedComponentState(string html) =>
        PersistedComponentStateMarker().Replace(html, string.Empty);

    [GeneratedRegex("<!--Blazor-Server-Component-State:[^>]*-->")]
    private static partial Regex PersistedComponentStateMarker();

    /// <summary>
    /// Asserts the response is the one page every unauthenticated request gets (AD21) — which is
    /// also the only shape an anonymous denial takes, since nothing redirects a stranger to login.
    /// </summary>
    public static async Task AssertIsAnonymousLandingPageAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnonymousLandingPage.Html, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Asserts the caller was served something of its own rather than the landing page.</summary>
    public static async Task AssertIsNotAnonymousLandingPageAsync(HttpResponseMessage response) =>
        Assert.NotEqual(AnonymousLandingPage.Html, await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Asserts a redirect to a path <em>on this site</em>.
    /// </summary>
    /// <remarks>
    /// The host check is the point, not a formality: an off-site redirect to
    /// <c>https://evil.example/</c> has an <see cref="Uri.AbsolutePath"/> of <c>"/"</c>, so an
    /// assertion that compared only the path would accept an open redirect as a redirect home.
    /// </remarks>
    public static void AssertRedirectedTo(string expectedPath, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = Assert.IsType<Uri>(response.Headers.Location);

        if (location.IsAbsoluteUri)
        {
            Assert.Equal(ZeroWikiAppFactory.BaseAddress.Authority, location.Authority);
        }

        Assert.Equal(expectedPath, location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString);
    }
}
