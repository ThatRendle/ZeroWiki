using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageIndex"/>, the process-memory singleton holder (D15) — both its low-level
/// swap (4.1–4.2) and its freshness-on-read behaviour against a real git repository (4.3): refreshing
/// when a writer that never notified the app moves <c>HEAD</c>, and single-flighting concurrent
/// refreshes rather than racing them.
/// </summary>
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
    public void Current_BeforeAnyReplace_IsTheEmptySnapshot()
    {
        using var index = new PageIndex(CreateBuilder());

        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public void Replace_InstallsTheNewSnapshotWholesale()
    {
        using var index = new PageIndex(CreateBuilder());
        var entry = new PageIndexEntry("route", "route.md", "/abs/route.md", "Title", ["tag"], null);
        var snapshot = new PageIndexSnapshot([entry], [], [], "deadbeef");

        index.Replace(snapshot);

        Assert.Same(snapshot, index.Current);
    }

    [Fact]
    public async Task GetCurrentAsync_WhenTheInstalledStampMatchesHead_ReturnsItWithoutRefreshing()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", "content");
        var builder = CreateBuilder();
        using var index = new PageIndex(builder);
        var installed = await builder.BuildAsync(CancellationToken.None);
        index.Replace(installed);

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
        using var index = new PageIndex(builder);
        index.Replace(await builder.BuildAsync(CancellationToken.None));

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
        using var index = new PageIndex(builder);
        index.Replace(await builder.BuildAsync(CancellationToken.None));

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
        using var index = new PageIndex(builder);
        index.Replace(await builder.BuildAsync(CancellationToken.None));

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
}
