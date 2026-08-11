using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// D19, §8 block B: <see cref="PushReactionService.ReactAsync"/> against a real git repository —
/// exactly the diff shape (<c>git diff --name-only</c> between two fixed shas) a caller inside
/// <c>GitSmartHttpEndpoints.HandleReceivePackAsync</c> would hand it, never something this type
/// re-derives from "current HEAD" itself (D19 §1). <see cref="PageChangeNotifierTests"/> covers the
/// notifier's own fan-out; this file covers only "were the right routes computed and handed to it."
/// </summary>
public sealed class PushReactionServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-push-reaction-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();
    private readonly RecordingPageChangeNotifier _notifier = new();

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private PushReactionService CreateService()
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);
        var frontmatterExtractor = new PageFrontmatterExtractor(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser());
        var builder = new PageIndexBuilder(
            paths,
            _git,
            new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance),
            frontmatterExtractor,
            historyService,
            NullLogger<PageIndexBuilder>.Instance);
        var pageIndex = new PageIndex(builder, NullLogger<PageIndex>.Instance);

        return new PushReactionService(
            paths, _git, historyService, pageIndex, _notifier, NullLogger<PushReactionService>.Instance);
    }

    /// <summary>Builds a service whose <see cref="PageIndex"/> is backed by <paramref name="builder"/>
    /// instead of a real <see cref="PageIndexBuilder"/> -- the seam that lets a test force the 8.1 warm
    /// to throw without needing to corrupt the repository the 8.2 diff also reads from.</summary>
    private PushReactionService CreateServiceWithPageIndexBuilder(IPageIndexBuilder builder)
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);
        var pageIndex = new PageIndex(builder, NullLogger<PageIndex>.Instance);

        return new PushReactionService(
            paths, _git, historyService, pageIndex, _notifier, NullLogger<PushReactionService>.Instance);
    }

    private async Task InitializeRepositoryAsync()
    {
        var repositoryRoot = Paths.RepositoryRoot;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));

        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.name", "placeholder"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.email", "placeholder@example.invalid"]);
    }

    private async Task<string> CommitAsync(string relativePath, string content)
    {
        var repositoryRoot = Paths.RepositoryRoot;
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "page"],
            new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());

        var head = await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"]);
        return head.StandardOutput.Trim();
    }

    private async Task<string> CurrentHeadAsync() =>
        (await _git.RunOrThrowAsync(Paths.RepositoryRoot, ["rev-parse", "HEAD"])).StandardOutput.Trim();

    [Fact]
    public async Task ReactAsync_BeforeEqualsAfter_NeverNotifies()
    {
        // D19 §2: `before == after` is what means "nothing to react to" -- the same sha twice, exactly
        // what a server-side-rejected or no-op push leaves the caller holding regardless of exit code.
        await InitializeRepositoryAsync();
        var sha = await CommitAsync("page.md", "content");

        await CreateService().ReactAsync(sha, sha, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_BothShasNull_NeverNotifies()
    {
        await CreateService().ReactAsync(null, null, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_ChangedPage_NotifiesExactlyItsRoute()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        var route = Assert.Single(call);
        Assert.Equal("page", route.Value);
    }

    [Fact]
    public async Task ReactAsync_MultiplePagesChanged_NotifiesAllOfThemAndNothingElse()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("untouched.md", "stays the same");

        var repositoryRoot = Paths.RepositoryRoot;
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "first.md"), "one");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "second.md"), "two");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot, ["commit", "-m", "two pages"], new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());
        var after = await CurrentHeadAsync();

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(2, call.Count);
        Assert.Contains(call, r => r.Value == "first");
        Assert.Contains(call, r => r.Value == "second");
        Assert.DoesNotContain(call, r => r.Value == "untouched");
    }

    [Fact]
    public async Task ReactAsync_ChangeUnderADotPrefixedDirectory_IsExcludedFromTheNotifiedRoutes()
    {
        // An Obsidian vault's .obsidian/ -- never a page candidate, so it can never name a route worth
        // notifying (mirrors PageEnumerationService/PageIndexBuilder's own rule).
        await InitializeRepositoryAsync();
        var before = await CommitAsync("page.md", "content");

        var repositoryRoot = Paths.RepositoryRoot;
        var dotDir = Path.Combine(repositoryRoot, "docs", ".obsidian");
        Directory.CreateDirectory(dotDir);
        await File.WriteAllTextAsync(Path.Combine(dotDir, "config.json"), "{}");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot, ["commit", "-m", "obsidian config"], new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());
        var after = await CurrentHeadAsync();

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        Assert.Empty(call);
    }

    [Fact]
    public async Task ReactAsync_UnresolvableShas_LogsAndDoesNotThrow()
    {
        await InitializeRepositoryAsync();
        await CommitAsync("page.md", "content");

        var bogusBefore = new string('a', 40);
        var bogusAfter = new string('b', 40);

        // Must not throw -- the push already landed and was already acknowledged; a diff this type
        // cannot compute is a warning, never a fault that escapes this method (D19 decision 2).
        await CreateService().ReactAsync(bogusBefore, bogusAfter, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_NullBeforeSha_WarmsTheIndexButNeverNotifies()
    {
        // An unborn-to-born HEAD transition (in practice unreachable once the app has started, since
        // startup always creates an initial commit first) -- there is no prior servable state anyone
        // could have been viewing, so there is nothing to diff or to notify.
        await InitializeRepositoryAsync();
        var after = await CommitAsync("page.md", "content");

        await CreateService().ReactAsync(null, after, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_IndexWarmThrows_StillComputesAndBroadcastsTheChangedRoutes()
    {
        // §8 remediation (supervisor finding): 8.1 (the index warm) and 8.2 (the spec-required diff
        // and broadcast) must fail independently. Forces the warm to throw via a fake IPageIndexBuilder
        // -- PageIndex.GetCurrentAsync calls ProbeCurrentHeadShaAsync directly and has nothing of its
        // own to catch a builder fault with -- and asserts the broadcast still happens with the correct
        // routes regardless. Fails if the two are ever re-coupled behind one try/catch.
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        await CreateServiceWithPageIndexBuilder(new ThrowingPageIndexBuilder())
            .ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        var route = Assert.Single(call);
        Assert.Equal("page", route.Value);
    }

    private sealed class ThrowingPageIndexBuilder : IPageIndexBuilder
    {
        public Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated index-warm failure.");

        public Task<PageIndexSnapshot> RefreshAsync(
            PageIndexSnapshot current, string? currentHeadSha, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated index-warm failure.");
    }

    private sealed class RecordingPageChangeNotifier : IPageChangeNotifier
    {
        public List<IReadOnlyCollection<EncodedRoute>> Calls { get; } = [];

        public IDisposable Subscribe(EncodedRoute route, Func<Task> onChanged) =>
            throw new NotSupportedException("PushReactionService never subscribes; only viewers do.");

        public Task NotifyChangedAsync(IReadOnlyCollection<EncodedRoute> routes, CancellationToken cancellationToken)
        {
            Calls.Add(routes);
            return Task.CompletedTask;
        }
    }
}
