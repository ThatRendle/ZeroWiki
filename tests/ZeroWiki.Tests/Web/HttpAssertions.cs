using System.Net;

namespace ZeroWiki.Tests.Web;

public static class HttpAssertions
{
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
