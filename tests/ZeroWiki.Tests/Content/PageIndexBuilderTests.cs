using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageIndexBuilder"/> against a real git repository and real files on disk —
/// the scenarios named in <c>specs/content-store/spec.md</c>'s <i>Derived index rebuildable from the
/// repository</i> requirement that this block (4.1-4.2) owns: rebuilding from the repository alone,
/// refusals riding in the same snapshot as pages, and an index with no content being empty rather than a
/// failure.
/// </summary>
public sealed class PageIndexBuilderTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-index-{Guid.NewGuid():n}");
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

    /// <summary>Bootstraps a non-bare repository with a <c>docs/</c> working tree, matching D8/§2.</summary>
    private async Task InitializeRepositoryAsync()
    {
        var repositoryRoot = Paths.RepositoryRoot;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));

        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.name", "placeholder"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.email", "placeholder@example.invalid"]);
    }

    private async Task<string> CommitPageAsync(string relativePath, GitAuthor author, DateTimeOffset authoredAt, string content)
    {
        var repositoryRoot = Paths.RepositoryRoot;
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);

        var env = new Dictionary<string, string>(author.ToEnvironmentVariables())
        {
            ["GIT_AUTHOR_DATE"] = authoredAt.ToString("O"),
            ["GIT_COMMITTER_DATE"] = authoredAt.ToString("O"),
        };

        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "page"], env);

        var head = await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"]);
        return head.StandardOutput.Trim();
    }

    [Fact]
    public async Task Build_PopulatesTitleTagsAndLastEditFromTheRepository()
    {
        await InitializeRepositoryAsync();
        var editedAt = new DateTimeOffset(2026, 5, 6, 8, 0, 0, TimeSpan.Zero);
        var expectedSha = await CommitPageAsync(
            "Kick Off.md",
            new GitAuthor("Alice", "alice@zerowiki.example"),
            editedAt,
            "---\ntitle: Kick Off\ntags: [meeting, project-x]\n---\n\n# Body\n");

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal("Kick_Off", page.Route);
        Assert.Equal("Kick Off.md", page.RelativePath);
        Assert.Equal("Kick Off", page.Title);
        Assert.Equal(["meeting", "project-x"], page.Tags);
        Assert.NotNull(page.LastEdit);
        Assert.Equal("Alice", page.LastEdit.AuthorName);
        Assert.Equal(editedAt, page.LastEdit.EditedAt);
        Assert.Equal(expectedSha, snapshot.CommitSha);
    }

    [Fact]
    public async Task Build_PageWithoutFrontmatter_HasNullTitleAndNoTags()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync(
            "plain.md",
            new GitAuthor("Alice", "alice@zerowiki.example"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "# Just a heading\n\nNo frontmatter.\n");

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        var page = Assert.Single(snapshot.Pages);
        Assert.Null(page.Title);
        Assert.Empty(page.Tags);
    }

    [Fact]
    public async Task Build_NonAsciiFilename_IsIndexedWithCorrectLastEdit()
    {
        // The full builder pipeline over the fault §2 shipped: git quotes non-ASCII paths in
        // --name-status output by default, which an ASCII-only fixture cannot see.
        await InitializeRepositoryAsync();
        var editedAt = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);
        await CommitPageAsync("Café Notes.md", new GitAuthor("Alice", "alice@zerowiki.example"), editedAt, "content");

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        var page = Assert.Single(snapshot.Pages);
        Assert.Equal("Café Notes.md", page.RelativePath);
        Assert.NotNull(page.LastEdit);
        Assert.Equal("Alice", page.LastEdit.AuthorName);
        Assert.Equal(editedAt, page.LastEdit.EditedAt);
    }

    [Fact]
    public async Task Build_FrontmatterLargerThanTheCap_YieldsEmptyMetadataRatherThanThrowing()
    {
        await InitializeRepositoryAsync();
        var oversizedTags = string.Join(", ", Enumerable.Range(0, 2000).Select(i => $"tag-{i}"));
        var markdown = $"---\ntitle: Should Not Appear\ntags: [{oversizedTags}]\n---\n\n# Body\n";
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(markdown) > SharpYamlFrontmatterParser.MaxSizeBytes,
            "fixture must exceed the cap to exercise truncation");

        await CommitPageAsync("big.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, markdown);

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        var page = Assert.Single(snapshot.Pages);
        Assert.Null(page.Title);
        Assert.Empty(page.Tags);
    }

    [Fact]
    public async Task Build_AmbiguousRouteAndUnreadableDirectory_SurviveIntoTheSnapshot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // chmod-based unreadability is a POSIX permissions concept; see
            // PageEnumerationServiceTests.UnreadableDirectory_IsReportedAndOtherPagesStayServed, whose
            // fixture idiom this test reuses rather than inventing a second one.
            return;
        }

        await InitializeRepositoryAsync();
        await CommitPageAsync("a_ b.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        await CommitPageAsync("a _b.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        await CommitPageAsync("unaffected.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");

        var lockedDir = Path.Combine(Paths.WorkingTree, "locked");
        Directory.CreateDirectory(lockedDir);
        await File.WriteAllTextAsync(Path.Combine(lockedDir, "secret.md"), "content");
        File.SetUnixFileMode(lockedDir, UnixFileMode.None);

        try
        {
            bool stillReadable;
            try
            {
                Directory.EnumerateFileSystemEntries(lockedDir).ToList();
                stillReadable = true;
            }
            catch (UnauthorizedAccessException)
            {
                stillReadable = false;
            }

            if (stillReadable)
            {
                // Running as a user that ignores permission bits (e.g. root in CI) — this test's premise
                // cannot be exercised in this environment; nothing to assert.
                return;
            }

            var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

            var ambiguous = Assert.Single(snapshot.AmbiguousRoutes);
            Assert.Equal("a___b", ambiguous.Route);
            Assert.DoesNotContain(snapshot.Pages, p => p.Route == "a___b");

            Assert.Contains("locked", snapshot.UnreadableDirectories);
            Assert.DoesNotContain(snapshot.Pages, p => p.Route.StartsWith("locked", StringComparison.Ordinal));

            Assert.Contains(snapshot.Pages, p => p.Route == "unaffected");
        }
        finally
        {
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Build_RepositoryRootIsNotAGitRepository_ThrowsRatherThanReturningTheEmptySnapshot()
    {
        // RepositoryRoot exists but was never `git init`ed: `git rev-parse --verify -q HEAD` exits 128
        // here ("fatal: not a git repository"), not 1 — a different failure mode than an unborn HEAD, and
        // one that must throw naming what failed rather than being folded into the same "no pages"
        // outcome an unborn HEAD gets.
        Directory.CreateDirectory(Paths.RepositoryRoot);

        await Assert.ThrowsAsync<GitProcessException>(() => CreateBuilder().BuildAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Build_UnbornHead_ReturnsTheEmptySnapshotRatherThanThrowing()
    {
        await InitializeRepositoryAsync();

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        Assert.Same(PageIndexSnapshot.Empty, snapshot);
        Assert.Empty(snapshot.Pages);
        Assert.Null(snapshot.CommitSha);
    }

    [Fact]
    public async Task Build_HeadBornButNoPages_ReturnsAnEmptyPagesListWithACommitSha()
    {
        await InitializeRepositoryAsync();
        await _git.RunOrThrowAsync(Paths.RepositoryRoot, ["commit", "--allow-empty", "-m", "init"]);

        var snapshot = await CreateBuilder().BuildAsync(CancellationToken.None);

        Assert.Empty(snapshot.Pages);
        Assert.NotNull(snapshot.CommitSha);
    }
}
