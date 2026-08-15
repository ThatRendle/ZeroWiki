using System.Net;
using System.Text.RegularExpressions;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 9.1: ties every occurrence of the git remote URL documented in <c>README.md</c>'s
/// "Syncing with Obsidian" section to the route <see cref="ZeroWiki.Web.GitSmartHttpEndpoints"/>
/// actually registers, so a route rename that leaves the prose untouched fails here instead of
/// only being discovered by a reader following stale instructions.
/// </summary>
/// <remarks>
/// The README is read from disk rather than duplicated as a string constant — the whole point is
/// to catch the file drifting from the code, which a copy of its text at the time this test was
/// written could never do. Every extracted path is driven at the real request pipeline
/// (<see cref="ZeroWikiAppFactory"/>) and checked for exactly the <c>401</c> the git routes are
/// documented, and independently tested (<c>GitSmartHttpAuthenticationTests</c>), to return for an
/// unauthenticated request — not merely "not 404", so a route that starts requiring something
/// other than basic auth (or stops requiring auth at all) also fails this test.
/// </remarks>
public sealed class ReadmeGitRemoteDocumentationTests
{
    [Fact]
    public async Task DocumentedCloneUrlPaths_AreRoutesTheAppActuallyServes()
    {
        var readme = await File.ReadAllTextAsync(FindReadmePath());

        // Matches every documented form of the remote URL: the bare canonical line, and every
        // prose/CLI occurrence with a username and/or token embedded ahead of `<your-host>`.
        var matches = Regex.Matches(
            readme,
            @"https://(?:<username>(?::<token>)?@)?<your-host>(?<path>/[^\s`)""',]+)");

        Assert.True(
            matches.Count >= 4,
            $"Expected at least 4 documented occurrences of the remote URL in README.md, found {matches.Count}.");

        var documentedPaths = matches.Select(m => m.Groups["path"].Value).Distinct();

        using var app = new ZeroWikiAppFactory();
        var client = app.CreateHttpClient();

        foreach (var documentedPath in documentedPaths)
        {
            var response = await client.GetAsync($"{documentedPath}/info/refs?service=git-upload-pack");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repository root (identified by
    /// <c>ZeroWiki.slnx</c>, the one file guaranteed to sit beside <c>README.md</c> regardless of
    /// build configuration or target framework folder depth) rather than hard-coding a relative
    /// path that would silently break if either moved.
    /// </summary>
    private static string FindReadmePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ZeroWiki.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root (ZeroWiki.slnx) above '{AppContext.BaseDirectory}'.");
        }

        return Path.Combine(directory.FullName, "README.md");
    }
}
