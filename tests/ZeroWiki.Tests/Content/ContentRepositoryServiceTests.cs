using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="ContentRepositoryService"/> directly against a throwaway temp directory —
/// the real <c>git</c> binary, not a fake. <see cref="ZeroWiki.Tests.Web.ContentRepositoryStartupTests"/>
/// covers the DI/<c>Program.cs</c> wiring instead of repeating these scenarios.
/// </summary>
public sealed class ContentRepositoryServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-content-repo-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyDataRoot_InitializesANonBareRepositoryWithOneSystemAuthoredCommit()
    {
        var service = CreateService();

        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        await AssertIsNonBareAsync(repositoryRoot);

        Assert.True(File.Exists(Path.Combine(repositoryRoot, "docs", ".gitkeep")));
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "docs/.gitkeep"]);
        Assert.Equal("docs/.gitkeep", lsFiles.StandardOutput.Trim());

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());

        var authorName = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%an"])).StandardOutput.Trim();
        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("System", authorName);
        Assert.Equal("system@zerowiki.org", authorEmail);

        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task SecondStart_DoesNotCreateASecondInitialCommit()
    {
        var service = CreateService();

        await service.EnsureRepositoryAsync();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertConfigurationIsAppliedAsync(repositoryRoot);
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task RepositoryCreatedWithoutTheConfiguration_GetsConfiguredOnTheNextStart()
    {
        // Stands in for a repository made by an older image, or restored from a backup, that
        // predates receive.denyCurrentBranch/http.receivepack being set.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", ContentRepositoryService.DefaultBranch]);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", ".gitkeep"), string.Empty);
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/.gitkeep"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "pre-existing commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Somebody Else",
                ["GIT_AUTHOR_EMAIL"] = "somebody@example.com",
                ["GIT_COMMITTER_NAME"] = "Somebody Else",
                ["GIT_COMMITTER_EMAIL"] = "somebody@example.com",
            });

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        await AssertConfigurationIsAppliedAsync(repositoryRoot);

        // The pre-existing commit was not touched — no second initial commit was layered on top.
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("somebody@example.com", authorEmail);

        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task BareRepository_FailsToStartNamingThePath()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "--bare"]);

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(repositoryRoot, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Branch_IsTheNamedConstantRegardlessOfTheHostsDefaultBranch()
    {
        // Scoped to this one `git init` subprocess via GitProcessRunner's own per-call environment
        // override — never Environment.SetEnvironmentVariable, which mutates the *process-wide*
        // environment that every concurrently-running test class's git subprocesses inherit under
        // xUnit's default parallelism. This exercises the exact invocation shape
        // ContentRepositoryService.EnsureRepositoryAsync uses (`init -b <DefaultBranch>`) to prove
        // the explicit `-b` wins over a host's `init.defaultBranch`, without any shared-state risk.
        var globalConfigPath = Path.Combine(_dataRoot, "global-gitconfig-for-test");
        Directory.CreateDirectory(_dataRoot);
        await File.WriteAllTextAsync(globalConfigPath, "[init]\n\tdefaultBranch = notmain\n");

        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["init", "-b", ContentRepositoryService.DefaultBranch],
            new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = globalConfigPath });

        var branch = (await _git.RunOrThrowAsync(repositoryRoot, ["branch", "--show-current"])).StandardOutput.Trim();
        Assert.Equal(ContentRepositoryService.DefaultBranch, branch);
        Assert.NotEqual("notmain", branch);
    }

    [Fact]
    public async Task DataRootNestedInsideAnAncestorGitRepository_InitializesItsOwnRepositoryInstead()
    {
        // The missing fixture: repositoryRoot has no .git of its own, but its parent directory does,
        // and that ancestor already has history — e.g. a relative ContentStorage:DataRoot resolved
        // under a developer's own checkout. `git rev-parse --is-bare-repository` run with
        // WorkingDirectory = repositoryRoot would discover the ancestor's .git (git's discovery walks
        // upward) and misreport "existing non-bare repo", so bootstrap must never ask git that
        // question before confirming repositoryRoot has an entry of its own.
        Directory.CreateDirectory(_dataRoot);
        await _git.RunOrThrowAsync(_dataRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(
            _dataRoot,
            ["commit", "--allow-empty", "-m", "ancestor commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Ancestor",
                ["GIT_AUTHOR_EMAIL"] = "ancestor@example.com",
                ["GIT_COMMITTER_NAME"] = "Ancestor",
                ["GIT_COMMITTER_EMAIL"] = "ancestor@example.com",
            });

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        await AssertIsNonBareAsync(repositoryRoot);
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "docs", ".gitkeep")));
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("system@zerowiki.org", authorEmail);
        await AssertConfigurationIsAppliedAsync(repositoryRoot);
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        // The ancestor repository must remain untouched — no configuration written into it, and its
        // own single commit unaffected by the nested repository's initial commit.
        var ancestorReceiveConfig = await _git.RunAsync(_dataRoot, ["config", "--get", "receive.denyCurrentBranch"]);
        Assert.False(ancestorReceiveConfig.Succeeded);
        Assert.Equal("1", (await _git.RunOrThrowAsync(_dataRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    private string RepositoryRoot => Path.Combine(_dataRoot, "wiki");

    private ContentRepositoryService CreateService() =>
        new(new ContentPaths(_dataRoot), _git, NullLogger<ContentRepositoryService>.Instance);

    private async Task AssertIsNonBareAsync(string repositoryRoot)
    {
        var result = await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "--is-bare-repository"]);
        Assert.Equal("false", result.StandardOutput.Trim());
    }

    private async Task AssertConfigurationIsAppliedAsync(string repositoryRoot)
    {
        Assert.Equal(
            "updateInstead",
            (await _git.RunOrThrowAsync(repositoryRoot, ["config", "receive.denyCurrentBranch"])).StandardOutput.Trim());
        Assert.Equal(
            "true",
            (await _git.RunOrThrowAsync(repositoryRoot, ["config", "http.receivepack"])).StandardOutput.Trim());
    }

    private async Task AssertPorcelainIsEmptyAsync(string repositoryRoot)
    {
        var status = await _git.RunOrThrowAsync(repositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }
}
