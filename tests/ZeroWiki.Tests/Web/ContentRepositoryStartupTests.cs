using ZeroWiki.Content;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Verifies <c>Program.cs</c>'s startup wiring — <c>EnsureContentRepositoryAsync</c> resolving the
/// real DI-registered <see cref="ContentRepositoryService"/> and <see cref="ContentPaths"/> — rather
/// than <see cref="ZeroWiki.Tests.Content.ContentRepositoryServiceTests"/>'s scenarios against the
/// service directly.
/// </summary>
public sealed class ContentRepositoryStartupTests
{
    private readonly GitProcessRunner _git = new();

    [Fact]
    public async Task Starting_the_app_initializes_the_content_repository_under_the_configured_data_root()
    {
        using var app = new ZeroWikiAppFactory();
        using var client = app.CreateHttpClient();
        await client.GetAsync("/");

        var repositoryRoot = Path.Combine(app.DataRoot, "wiki");
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "docs", ".gitkeep")));

        var status = await _git.RunOrThrowAsync(repositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }

    [Fact]
    public async Task Restarting_the_app_does_not_create_a_second_initial_commit()
    {
        using var app = new ZeroWikiAppFactory();
        using var client = app.CreateHttpClient();
        await client.GetAsync("/");

        using var restarted = ZeroWikiAppFactory.RestartedFrom(app);
        using var restartedClient = restarted.CreateHttpClient();
        await restartedClient.GetAsync("/");

        var repositoryRoot = Path.Combine(app.DataRoot, "wiki");
        var commitCount = await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"]);
        Assert.Equal("1", commitCount.StandardOutput.Trim());
    }
}
