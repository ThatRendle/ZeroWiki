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
    /// Strips the <c>&lt;!--Blazor:{...}--&gt;</c> start/end marker comments the framework wraps every
    /// <c>InteractiveServer</c> component instance in — a second, independent source of
    /// DataProtection-protected (hence effectively random) bytes, distinct from
    /// <see cref="StripPersistedComponentState"/>'s end-of-document marker. §8 block C's first
    /// case-insensitive/short-substring assertion against a real page's body (one that renders
    /// <c>ChangedOnDiskIndicator</c>, D19 §3's first <c>InteractiveServer</c> island) hit a
    /// reproducible ~1-in-several flake from this marker's <c>"descriptor"</c> field coincidentally
    /// containing the needle; <see cref="StripPersistedComponentState"/> alone does not cover it,
    /// because until that assertion no test's body check was both case-insensitive/short <em>and</em>
    /// against a response carrying an interactive component's own marker. Non-greedy — the marker's
    /// base64-encoded fields never contain <c>-</c> (standard alphabet: <c>A-Za-z0-9+/=</c>), so
    /// <c>"}-->"</c> cannot appear inside one and prematurely close the match early.
    /// </summary>
    public static string StripInteractiveComponentMarkers(string html) =>
        InteractiveComponentMarker().Replace(html, string.Empty);

    [GeneratedRegex(@"<!--Blazor:\{[\s\S]*?\}-->")]
    private static partial Regex InteractiveComponentMarker();

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
