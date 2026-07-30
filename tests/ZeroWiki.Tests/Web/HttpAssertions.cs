using System.Net;
using ZeroWiki.Web;

namespace ZeroWiki.Tests.Web;

public static class HttpAssertions
{
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
