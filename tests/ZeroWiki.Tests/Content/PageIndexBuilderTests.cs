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

    [Fact]
    public async Task Refresh_WhenHeadUnchanged_ReturnsTheSameSnapshotInstance()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);

        var refreshed = await builder.RefreshAsync(current, current.CommitSha, CancellationToken.None);

        Assert.Same(current, refreshed);
    }

    [Fact]
    public async Task Refresh_AddedPage_IsAddedWithoutDisturbingUnaffectedPages()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("first.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);

        var editedAt = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var newSha = await CommitPageAsync("second.md", new GitAuthor("Bob", "bob@zerowiki.example"), editedAt, "new content");

        var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

        Assert.Equal(newSha, refreshed.CommitSha);
        Assert.Equal(2, refreshed.Pages.Count);
        var added = Assert.Single(refreshed.Pages, p => p.RelativePath == "second.md");
        Assert.NotNull(added.LastEdit);
        Assert.Equal("Bob", added.LastEdit.AuthorName);
        Assert.Contains(refreshed.Pages, p => p.RelativePath == "first.md");
    }

    [Fact]
    public async Task Refresh_ModifiedPage_PicksUpNewFrontmatterAndLastEdit()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync(
            "page.md",
            new GitAuthor("Alice", "alice@zerowiki.example"),
            DateTimeOffset.UnixEpoch,
            "---\ntitle: Original\n---\nBody.");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);

        var editedAt = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var newSha = await CommitPageAsync(
            "page.md",
            new GitAuthor("Carol", "carol@zerowiki.example"),
            editedAt,
            "---\ntitle: Updated\n---\nBody.");

        var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

        var page = Assert.Single(refreshed.Pages);
        Assert.Equal("Updated", page.Title);
        Assert.NotNull(page.LastEdit);
        Assert.Equal("Carol", page.LastEdit.AuthorName);
        Assert.Equal(editedAt, page.LastEdit.EditedAt);
    }

    [Fact]
    public async Task Refresh_DeletedPage_LeavesTheIndexRatherThanServingAGoneFile()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("keep.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        await CommitPageAsync("remove.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);
        Assert.Equal(2, current.Pages.Count);

        var repositoryRoot = Paths.RepositoryRoot;
        await _git.RunOrThrowAsync(repositoryRoot, ["rm", "docs/remove.md"]);
        var env = new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables();
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "remove"], env);
        var newSha = (await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"])).StandardOutput.Trim();

        var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

        var remaining = Assert.Single(refreshed.Pages);
        Assert.Equal("keep.md", remaining.RelativePath);
    }

    [Fact]
    public async Task Refresh_NewFileCollidingWithAnExistingUnaffectedPage_RefusesBothRatherThanServingEither()
    {
        // The route a fresh EnumeratePages() would compute for "a_ b.md" and "a _b.md" collide (D12).
        // Only the second file is part of the diff here -- the first is entirely unaffected -- proving
        // the incremental path detects an emergent collision against a claimant the diff never mentions.
        await InitializeRepositoryAsync();
        await CommitPageAsync("a_ b.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "first");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);
        Assert.Single(current.Pages);
        Assert.Empty(current.AmbiguousRoutes);

        var newSha = await CommitPageAsync("a _b.md", new GitAuthor("Bob", "bob@zerowiki.example"), DateTimeOffset.UnixEpoch, "second");

        var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

        Assert.Empty(refreshed.Pages);
        var ambiguous = Assert.Single(refreshed.AmbiguousRoutes);
        Assert.Equal("a___b", ambiguous.Route);
        Assert.Equal(["a _b.md", "a_ b.md"], ambiguous.RelativePaths);
    }

    [Fact]
    public async Task Refresh_RemovingOneOfTwoCollidingClaimants_ServesTheRemainingOne()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("a_ b.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "first");
        await CommitPageAsync("a _b.md", new GitAuthor("Bob", "bob@zerowiki.example"), DateTimeOffset.UnixEpoch, "second");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);
        Assert.Empty(current.Pages);
        Assert.Single(current.AmbiguousRoutes);

        var repositoryRoot = Paths.RepositoryRoot;
        await _git.RunOrThrowAsync(repositoryRoot, ["rm", "docs/a _b.md"]);
        var env = new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables();
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "resolve collision"], env);
        var newSha = (await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"])).StandardOutput.Trim();

        var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

        Assert.Empty(refreshed.AmbiguousRoutes);
        var page = Assert.Single(refreshed.Pages);
        Assert.Equal("a_ b.md", page.RelativePath);
    }

    [Fact]
    public async Task Refresh_NoPreviousStamp_FallsBackToAFullRebuild()
    {
        await InitializeRepositoryAsync();
        var newSha = await CommitPageAsync("page.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        var builder = CreateBuilder();

        var refreshed = await builder.RefreshAsync(PageIndexSnapshot.Empty, newSha, CancellationToken.None);

        Assert.Equal(newSha, refreshed.CommitSha);
        Assert.Single(refreshed.Pages);
    }

    [Fact]
    public async Task Refresh_StampedCommitNoLongerResolvable_FallsBackToAFullRebuildRatherThanReportingNoChange()
    {
        await InitializeRepositoryAsync();
        await CommitPageAsync("page.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        var builder = CreateBuilder();
        var current = await builder.BuildAsync(CancellationToken.None);

        var editedAt = new DateTimeOffset(2026, 4, 4, 0, 0, 0, TimeSpan.Zero);
        var newSha = await CommitPageAsync("second.md", new GitAuthor("Bob", "bob@zerowiki.example"), editedAt, "content");

        // A stamp git can no longer resolve -- history rewritten, gc'd, or a reset --hard behind the
        // app's back -- must not silently degrade into "nothing changed": detected honestly and rebuilt.
        var unresolvableStamp = new string('f', 40);

        var refreshed = await builder.RefreshAsync(current with { CommitSha = unresolvableStamp }, newSha, CancellationToken.None);

        Assert.Equal(newSha, refreshed.CommitSha);
        Assert.Equal(2, refreshed.Pages.Count);
    }

    [Fact]
    public async Task Refresh_UnreadableDirectoriesPresentAtTheStampedSnapshot_FallsBackToAFullRebuild()
    {
        // Reviewer finding (block B round 1): ApplyIncrementalUpdateAsync reconstructs a route's other
        // claimants from `current` alone, which is sound only when `current` is a complete census of the
        // tree. It is not when UnreadableDirectories is non-empty -- a file under a directory this
        // process could not read at the last full build was never recorded anywhere in the snapshot. If
        // that directory later becomes readable (a permissions change, not a git-tracked one -- no `git
        // diff` will ever name it) this is the only way that file is ever rediscovered, proven here by
        // making the directory readable again between the stamped snapshot and the refresh and asserting
        // the previously-hidden page appears -- something only a full rebuild, never the incremental path,
        // could ever produce.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        await InitializeRepositoryAsync();
        await CommitPageAsync("unaffected.md", new GitAuthor("Alice", "alice@zerowiki.example"), DateTimeOffset.UnixEpoch, "content");
        await CommitPageAsync(
            Path.Combine("locked", "hidden.md"),
            new GitAuthor("Alice", "alice@zerowiki.example"),
            DateTimeOffset.UnixEpoch,
            "content");

        var lockedDir = Path.Combine(Paths.WorkingTree, "locked");
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
                // Running as a user that ignores permission bits (e.g. root in CI) -- this test's
                // premise cannot be exercised in this environment; nothing to assert.
                return;
            }

            var builder = CreateBuilder();
            var current = await builder.BuildAsync(CancellationToken.None);
            Assert.Contains("locked", current.UnreadableDirectories);
            Assert.DoesNotContain(current.Pages, p => p.RelativePath.Contains("hidden", StringComparison.Ordinal));

            // A diffed change entirely unrelated to "locked" -- plus the directory becoming readable
            // again (an operator's permissions fix, invisible to `git diff`) between the stamp and now.
            var newSha = await CommitPageAsync(
                "second.md",
                new GitAuthor("Bob", "bob@zerowiki.example"),
                DateTimeOffset.UnixEpoch,
                "content");
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var refreshed = await builder.RefreshAsync(current, newSha, CancellationToken.None);

            Assert.Empty(refreshed.UnreadableDirectories);
            Assert.Contains(refreshed.Pages, p => p.RelativePath.Contains("hidden", StringComparison.Ordinal));
            Assert.Contains(refreshed.Pages, p => p.RelativePath == "second.md");
        }
        finally
        {
            if (Directory.Exists(lockedDir))
            {
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
