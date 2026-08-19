using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageIndex"/>, the process-memory singleton holder (D15) — its low-level install
/// (4.1–4.2), its freshness-on-read behaviour against a real git repository (4.3), and D17's
/// compare-and-swap install mechanism (§6 block C2): discarding a refresh that raced a concurrent
/// invalidation, retrying bounded, and single-flighting concurrent refreshes rather than racing them.
/// </summary>
/// <remarks>
/// The CAS-race tests below drive the race through <see cref="PageIndex.GetCurrentAsync"/> itself, using
/// a fake <see cref="IPageIndexBuilder"/> whose <c>RefreshAsync</c> calls <see cref="PageIndex.Invalidate"/>
/// mid-flight — deterministic (the fake controls exactly when the invalidation fires, no timing involved)
/// and through the real public API, rather than granting test code access to <see cref="PageIndex"/>'s
/// own internals (§6 block C2 remediation, reviewer adjudication).
/// </remarks>
public sealed class PageIndexTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-index-holder-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private PageIndexBuilder CreateBuilder()
    {
        var paths = Paths;
        var frontmatterExtractor = new PageFrontmatterExtractor(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser());

        return new PageIndexBuilder(
            paths,
            _git,
            new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance),
            frontmatterExtractor,
            new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance),
            NullLogger<PageIndexBuilder>.Instance);
    }

    private static PageIndex CreateIndex(IPageIndexBuilder? builder = null) =>
        new(builder ?? CreateUnusedBuilder(), NullLogger<PageIndex>.Instance);

    /// <summary>
    /// A real (never faked) <see cref="PageIndexBuilder"/> for tests that exercise only <see cref="PageIndex"/>'s
    /// install mechanics and never call <see cref="PageIndex.GetCurrentAsync"/> — the one path that would
    /// actually invoke it. Pointed at a data root with no repository at all: safe precisely because it is
    /// never invoked in those tests.
    /// </summary>
    private static PageIndexBuilder CreateUnusedBuilder()
    {
        var paths = new ContentPaths(Path.Combine(Path.GetTempPath(), $"zerowiki-page-index-unused-{Guid.NewGuid():n}"));
        var git = new GitProcessRunner();

        return new PageIndexBuilder(
            paths,
            git,
            new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance),
            new PageFrontmatterExtractor(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser()),
            new PageHistoryService(paths, git, NullLogger<PageHistoryService>.Instance),
            NullLogger<PageIndexBuilder>.Instance);
    }

    private async Task InitializeRepositoryAsync()
    {
        var repositoryRoot = Paths.RepositoryRoot;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));

        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.name", "placeholder"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.email", "placeholder@example.invalid"]);
    }

    private async Task<string> CommitPageAsync(string relativePath, string content)
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

    [Fact]
    public void Current_BeforeAnyInstall_IsTheEmptySnapshot()
    {
        using var index = CreateIndex();

        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public void InstallStartupSnapshot_InstallsTheNewSnapshotWholesale()
    {
        using var index = CreateIndex();
        // PageRouteCodec.Encode("route.md") legitimately produces the "route" value this fixture needs —
        // no InternalsVisibleTo-dependent construction required (reviewer recommendation, §6 block C1 delta).
        var entry = new PageIndexEntry(PageRouteCodec.Encode("route.md"), "route.md", "/abs/route.md", "Title", ["tag"], null);
        var snapshot = new PageIndexSnapshot([entry], [], [], "deadbeef");

        index.InstallStartupSnapshot(snapshot);

        Assert.Same(snapshot, index.Current);
    }

    [Fact]
    public void Invalidate_InstallsTheEmptySnapshot()
    {
        using var index = CreateIndex();
        var entry = new PageIndexEntry(PageRouteCodec.Encode("route.md"), "route.md", "/abs/route.md", "Title", ["tag"], null);
        index.InstallStartupSnapshot(new PageIndexSnapshot([entry], [], [], "deadbeef"));

        index.Invalidate();

        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public async Task GetCurrentAsync_WhenTheInstalledStampMatchesHead_ReturnsItWithoutRefreshing()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "content");
        var builder = CreateBuilder();
        using var index = CreateIndex(builder);
        var installed = await builder.BuildAsync(CancellationToken.None);
        index.InstallStartupSnapshot(installed);

        var result = await index.GetCurrentAsync(CancellationToken.None);

        // Reference-equal, not merely value-equal: proves no refresh (which would build a distinct
        // record) ran at all, since HEAD had not moved since `installed` was built.
        Assert.Same(installed, result);
    }

    [Fact]
    public async Task GetCurrentAsync_ContentChangedByAWriterThatNeverNotifiedTheIndex_IsReflectedWithoutARestart()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "original");
        var builder = CreateBuilder();
        using var index = CreateIndex(builder);
        index.InstallStartupSnapshot(await builder.BuildAsync(CancellationToken.None));

        // A writer that never tells the app: a second commit made directly with git, exactly like an
        // `updateInstead` push or an operator committing on the volume (D15).
        var newSha = await CommitPageAsync("second.md", "added behind the app's back");

        var refreshed = await index.GetCurrentAsync(CancellationToken.None);

        Assert.Equal(newSha, refreshed.CommitSha);
        Assert.Equal(2, refreshed.Pages.Count);
        Assert.Contains(refreshed.Pages, p => p.RelativePath == "second.md");
        Assert.Same(refreshed, index.Current);
    }

    [Fact]
    public async Task GetCurrentAsync_DeletedPage_NoLongerAppearsInTheRefreshedSnapshot()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "content");
        var builder = CreateBuilder();
        using var index = CreateIndex(builder);
        index.InstallStartupSnapshot(await builder.BuildAsync(CancellationToken.None));

        var repositoryRoot = Paths.RepositoryRoot;
        await _git.RunOrThrowAsync(repositoryRoot, ["rm", "docs/page.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "remove"],
            new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());

        var refreshed = await index.GetCurrentAsync(CancellationToken.None);

        Assert.Empty(refreshed.Pages);
    }

    [Fact]
    public async Task GetCurrentAsync_ConcurrentCallsOnAStaleStamp_SingleFlightTheRefresh()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "original");
        var builder = CreateBuilder();
        using var index = CreateIndex(builder);
        index.InstallStartupSnapshot(await builder.BuildAsync(CancellationToken.None));

        await CommitPageAsync("second.md", "added behind the app's back");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => index.GetCurrentAsync(CancellationToken.None)));

        // Every concurrent caller observes the exact same snapshot instance -- if two callers had each
        // run their own refresh, both would build a distinct (if value-equal) PageIndexSnapshot record,
        // which Assert.Same would catch.
        var first = results[0];
        Assert.All(results, result => Assert.Same(first, result));
        Assert.Equal(2, first.Pages.Count);
    }

    [Fact]
    public async Task GetCurrentAsync_AfterInvalidate_PerformsAFullRebuildRatherThanAnIncrementalDiff()
    {
        // D17: a rollback's Invalidate() is the one content-changing-adjacent event that does not
        // advance HEAD, so the next GetCurrentAsync must take D15's no-previous-stamp branch (a full
        // rebuild) rather than trusting an incremental diff that could never have named the path a
        // rollback just restored.
        await InitializeRepositoryAsync();
        var headSha = await CommitPageAsync("page.md", "content");
        var builder = CreateBuilder();
        using var index = CreateIndex(builder);
        index.InstallStartupSnapshot(await builder.BuildAsync(CancellationToken.None));

        index.Invalidate();
        Assert.Same(PageIndexSnapshot.Empty, index.Current);

        var refreshed = await index.GetCurrentAsync(CancellationToken.None);

        Assert.Equal(headSha, refreshed.CommitSha);
        Assert.Single(refreshed.Pages);
        Assert.Equal("page.md", refreshed.Pages[0].RelativePath);
    }

    [Fact]
    public async Task GetCurrentAsync_WhenARollbackInvalidatesWhileARefreshIsInFlight_DiscardsThePoisonedRefreshAndRetries()
    {
        // The mutation target this block's brief names ("the generation check on install"): D17's own
        // race. A refresh captures the installed state, then -- before its own install runs -- a
        // rollback's Invalidate() lands. The refresh's result must be discarded rather than silently
        // overwriting the invalidation with metadata read over bytes the invalidation has since disowned.
        //
        // The fake builder below hands back a deliberately *wrong* ("poisoned") snapshot on its first
        // call and invalidates in the same call, before returning -- simulating a refresh that already
        // read a save's dirty, about-to-be-rolled-back bytes off disk. If the poisoned result is
        // installed anyway, the final result contains a page that was never real; if it is correctly
        // discarded and retried, the real (empty) index rebuilds from the actual, un-poisoned repository.
        await InitializeRepositoryAsync();
        var realBuilder = CreateBuilder();
        var raceBuilder = new PoisoningOnFirstCallBuilder(realBuilder);
        using var index = CreateIndex(raceBuilder);
        raceBuilder.Index = index;
        index.InstallStartupSnapshot(await realBuilder.BuildAsync(CancellationToken.None));

        await CommitPageAsync("real.md", "genuinely committed content");

        var result = await index.GetCurrentAsync(CancellationToken.None);

        Assert.DoesNotContain(result.Pages, p => p.RelativePath == "poisoned.md");
        Assert.Contains(result.Pages, p => p.RelativePath == "real.md");
    }

    [Fact]
    public async Task GetCurrentAsync_WhenInvalidationRacesEveryAttempt_ExhaustsTheBoundAndServesEmpty()
    {
        // D17: "never serve a discarded snapshot", not "always succeed" -- if invalidations keep landing
        // faster than a refresh can install (here: every single attempt, driven deterministically by the
        // fake), GetCurrentAsync must give up after its bound rather than loop forever or, worse, install
        // stale metadata anyway.
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "content");
        var realBuilder = CreateBuilder();
        var raceBuilder = new AlwaysRacingBuilder(realBuilder);
        using var index = CreateIndex(raceBuilder);
        raceBuilder.Index = index;
        index.InstallStartupSnapshot(await realBuilder.BuildAsync(CancellationToken.None));

        await CommitPageAsync("second.md", "added behind the app's back");

        var result = await index.GetCurrentAsync(CancellationToken.None);

        Assert.Same(PageIndexSnapshot.Empty, result);
    }

    /// <summary>
    /// Delegates <see cref="IPageIndexBuilder.RefreshAsync"/> to a real <see cref="PageIndexBuilder"/> on
    /// every call, except the first: the first call returns a hand-crafted, deliberately wrong snapshot
    /// (claiming a page — <c>poisoned.md</c> — that does not exist in the repository) and calls
    /// <see cref="PageIndex.Invalidate"/> before returning it, simulating a rollback's invalidation
    /// landing while that refresh was still in flight (D17).
    /// </summary>
    private sealed class PoisoningOnFirstCallBuilder(PageIndexBuilder real) : IPageIndexBuilder
    {
        private bool _hasPoisonedOnce;

        public PageIndex? Index { get; set; }

        public Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken) =>
            real.ProbeCurrentHeadShaAsync(cancellationToken);

        public Task<PageIndexSnapshot> RefreshAsync(PageIndexSnapshot current, string? currentHeadSha, CancellationToken cancellationToken)
        {
            if (!_hasPoisonedOnce)
            {
                _hasPoisonedOnce = true;

                var poisoned = new PageIndexSnapshot(
                    [new PageIndexEntry(PageRouteCodec.Encode("poisoned.md"), "poisoned.md", "/nonexistent/poisoned.md", "Poisoned", [], null)],
                    [],
                    [],
                    currentHeadSha);

                (Index ?? throw new InvalidOperationException($"{nameof(Index)} must be set before use.")).Invalidate();
                return Task.FromResult(poisoned);
            }

            return real.RefreshAsync(current, currentHeadSha, cancellationToken);
        }
    }

    /// <summary>
    /// Delegates <see cref="IPageIndexBuilder.RefreshAsync"/> to a real <see cref="PageIndexBuilder"/>,
    /// but calls <see cref="PageIndex.Invalidate"/> immediately before every single call returns —
    /// deterministically racing every one of <see cref="PageIndex.GetCurrentAsync"/>'s bounded attempts,
    /// never just the first.
    /// </summary>
    private sealed class AlwaysRacingBuilder(PageIndexBuilder real) : IPageIndexBuilder
    {
        public PageIndex? Index { get; set; }

        public Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken) =>
            real.ProbeCurrentHeadShaAsync(cancellationToken);

        public async Task<PageIndexSnapshot> RefreshAsync(PageIndexSnapshot current, string? currentHeadSha, CancellationToken cancellationToken)
        {
            var refreshed = await real.RefreshAsync(current, currentHeadSha, cancellationToken);
            (Index ?? throw new InvalidOperationException($"{nameof(Index)} must be set before use.")).Invalidate();
            return refreshed;
        }
    }
}
