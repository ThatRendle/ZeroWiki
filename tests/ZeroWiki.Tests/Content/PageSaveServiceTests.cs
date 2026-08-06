using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZeroWiki.Content;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageSaveService"/> (D4, D9, D16, D17, §6 block C2) against a real git repository
/// — every named scenario in <c>specs/content-editing/spec.md</c> except the save-point one (a Static
/// SSR form posting once, block D's concern, not this class's), plus the two hazards D17 names for this
/// block (a symlink at the resolved path, a non-canonical route value) and the ordering guarantee a
/// failed commit depends on.
/// </summary>
public sealed class PageSaveServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-save-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    private ContentPaths Paths => new(_dataRoot);
    private string RepositoryRoot => Paths.RepositoryRoot;
    private string WorkingTree => Paths.WorkingTree;

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Save_NewPageOnAbsentBase_CreatesOneCommitAuthoredAsTheSavingUser()
    {
        await InitializeRepositoryAsync();
        var service = CreateService(out _);
        var revisionCountBefore = await RevisionCountAsync();

        var result = await service.SaveAsync(
            new RouteValue("newpage"),
            "# Hello\n",
            PageBaseRevision.AbsentAtHead,
            Author("alice"),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.NotNull(result.CommitSha);
        Assert.Equal("# Hello\n", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "newpage.md")));
        Assert.Equal(revisionCountBefore + 1, await RevisionCountAsync());

        var authorName = (await _git.RunOrThrowAsync(RepositoryRoot, ["log", "-1", "--format=%an"])).StandardOutput.Trim();
        var authorEmail = (await _git.RunOrThrowAsync(RepositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("alice", authorName);
        Assert.Equal($"alice@{ContentAuthorshipOptions.DefaultHostDomain}", authorEmail);
    }

    [Fact]
    public async Task Save_ExistingPageOnCurrentBase_UpdatesTheFileAndCommits()
    {
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "v1");

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "v2",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.NotNull(result.CommitSha);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
    }

    [Fact]
    public async Task Save_OnStaleBase_IsRejectedWithConflictAndWritesNothing()
    {
        await InitializeRepositoryAsync();
        var staleSha = await CommitPageDirectlyAsync("page.md", "v1");

        // Another browser save or an incoming push changed it (D4's scenario), behind this save's back.
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "page.md"), "v2");
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", "docs/page.md"]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "someone else's edit"],
            new GitAuthor("Someone Else", "else@zerowiki.example").ToEnvironmentVariables());

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "attempted overwrite based on the stale v1",
            PageBaseRevision.ForBlob(staleSha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
    }

    [Fact]
    public async Task Save_DeclaredAbsentBaseButThePageNowExists_IsRejectedWithConflict()
    {
        // The mirror image of the stale-base scenario: the save believes it is creating a brand new
        // page, but someone else already created it at that same route while this save was in flight.
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync("page.md", "already created by someone else");

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "attempted creation",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Equal("already created by someone else", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
    }

    [Fact]
    public async Task Save_ByteIdenticalContent_ReportsSuccessWithNoCommitAndLeavesTheTreeClean()
    {
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "unchanged content");
        var revisionCountBefore = await RevisionCountAsync();

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "unchanged content",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Null(result.CommitSha);
        Assert.Equal(revisionCountBefore, await RevisionCountAsync());
        await AssertPorcelainIsEmptyAsync();
    }

    [Fact]
    public async Task Save_WhenCommitFailsForAnExistingPage_RestoresTheCommittedContentAndInvalidatesTheIndex()
    {
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "original content");
        await InstallFailingPreCommitHookAsync();

        var service = CreateService(out var index);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "this will never be committed",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
        await AssertPorcelainIsEmptyAsync();
        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public async Task Save_WhenCommitFailsForABrandNewPage_DeletesTheWrittenFileAndInvalidatesTheIndex()
    {
        await InitializeRepositoryAsync();
        await InstallFailingPreCommitHookAsync();

        var service = CreateService(out var index);
        var result = await service.SaveAsync(
            new RouteValue("brandnew"),
            "this will never be committed",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(WorkingTree, "brandnew.md")));
        await AssertPorcelainIsEmptyAsync();
        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public async Task Save_WhenTheWriteLockIsHeldByAnotherWriter_ReturnsRepositoryBusyAndWritesNothing()
    {
        await InitializeRepositoryAsync();
        using var externalLock = await RepositoryWriteLock.AcquireAsync(Paths.LockFilePath, TimeSpan.FromSeconds(5));

        var service = CreateService(out _, saveWriteLockTimeout: TimeSpan.FromMilliseconds(200));
        var result = await service.SaveAsync(
            new RouteValue("newpage"),
            "content",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.RepositoryBusy, result.Outcome);
        Assert.False(File.Exists(Path.Combine(WorkingTree, "newpage.md")));
    }

    [Fact]
    public async Task Save_WhenTheRequestIsCancelledAfterTheLockIsHeld_StillCompletesAndCommits()
    {
        // §6 block D2, Product Owner decision: once SaveAsync holds the write lock, the caller's own
        // cancellation stops applying. A pre-commit hook signals the moment it starts running -- proof
        // the save is already past lock acquisition, the write, staging and the no-op diff check, deep
        // inside the "commit" git subprocess -- before the test cancels. If the fix regressed (the
        // critical section started honouring the caller's token again), the cancelled "commit"
        // invocation would throw, and this save would roll back instead of completing.
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "v1");

        var startedMarkerPath = Path.Combine(Path.GetTempPath(), $"zerowiki-hook-started-{Guid.NewGuid():n}");
        await InstallHookThatSignalsThenSleepsAsync(startedMarkerPath, sleepSeconds: 1);

        try
        {
            var service = CreateService(out _);
            using var cts = new CancellationTokenSource();

            var saveTask = service.SaveAsync(
                new RouteValue("page"),
                "v2",
                PageBaseRevision.ForBlob(sha),
                Author(),
                cts.Token);

            await WaitForFileAsync(startedMarkerPath);
            cts.Cancel();

            var result = await saveTask;

            Assert.Equal(SaveOutcome.Saved, result.Outcome);
            Assert.NotNull(result.CommitSha);
            Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
            await AssertPorcelainIsEmptyAsync();
        }
        finally
        {
            if (File.Exists(startedMarkerPath))
            {
                File.Delete(startedMarkerPath);
            }
        }
    }

    [Fact]
    public async Task Save_UnresolvableRouteValue_IsRefusedAndWritesNothing()
    {
        await InitializeRepositoryAsync();
        var service = CreateService(out _);

        var result = await service.SaveAsync(
            new RouteValue(string.Empty),
            "content",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Save_RouteValueThatIsNotCanonicalForItsResolvedFile_IsRefused()
    {
        // File "a b.md"'s canonical route is "a_b" (space -> '_', D12). A route VALUE carrying a
        // literal space -- what ASP.NET Core routing would already have decoded a raw %20 into by the
        // time this class sees it -- also resolves (via TryDecodeRouteValue, which never touches a
        // non-underscore character) to the very same "a b.md", but is not that file's canonical route
        // (S2, D17): re-opening D12's collision through the save door is exactly what this refusal
        // exists to close.
        await InitializeRepositoryAsync();
        var service = CreateService(out _);

        var result = await service.SaveAsync(
            new RouteValue("a b"),
            "content",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.False(File.Exists(Path.Combine(WorkingTree, "a b.md")));
    }

    [Fact]
    public async Task Save_ATreeAtTheResolvedPath_IsRefusedRatherThanGuessed()
    {
        // A directory literally named "weird.md" tracked in git makes `ls-tree HEAD -- docs/weird.md`
        // report "tree <sha>" -- D17's catch-all, not one of the two states this class knows how to act
        // on (present blob / absent).
        await InitializeRepositoryAsync();
        var innerDirectory = Path.Combine(WorkingTree, "weird.md");
        Directory.CreateDirectory(innerDirectory);
        await File.WriteAllTextAsync(Path.Combine(innerDirectory, "inner.md"), "nested");
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", "docs/weird.md/inner.md"]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "weird"],
            new GitAuthor("Seed", "seed@zerowiki.example").ToEnvironmentVariables());

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("weird"),
            "content",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task Save_ASymlinkAtTheResolvedLeafPath_IsRefusedAndDoesNotFollowItOutOfDocs()
    {
        // D17's own named hazard: git models a symlink as a blob, so it can arrive by push, sit at a
        // save's resolved path, and read as an ordinary present blob to the base-revision probe.
        await InitializeRepositoryAsync();
        var secretPath = Path.Combine(_dataRoot, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "original secret");

        var symlinkPath = Path.Combine(WorkingTree, "evil.md");
        File.CreateSymbolicLink(symlinkPath, secretPath);
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", "docs/evil.md"]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "planted by a push"],
            new GitAuthor("Attacker", "attacker@zerowiki.example").ToEnvironmentVariables());
        var blobSha = await ReadHeadBlobShaAsync("docs/evil.md");

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("evil"),
            "malicious content",
            PageBaseRevision.ForBlob(blobSha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal("original secret", await File.ReadAllTextAsync(secretPath));
    }

    [Fact]
    public async Task Save_ASymlinkedAncestorDirectory_IsRefusedAndDoesNotWriteThroughIt()
    {
        // The same hazard one level up: PageRouteCodec's containment check is lexical (Path.GetFullPath
        // does not resolve symlinks), so it cannot by itself catch a symlinked ancestor directory
        // smuggling the write outside docs/.
        await InitializeRepositoryAsync();
        var outsideDirectory = Path.Combine(_dataRoot, "outside");
        Directory.CreateDirectory(outsideDirectory);

        var symlinkedSubdirectory = Path.Combine(WorkingTree, "linked");
        Directory.CreateSymbolicLink(symlinkedSubdirectory, outsideDirectory);

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("linked/newpage"),
            "malicious content",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.False(File.Exists(Path.Combine(outsideDirectory, "newpage.md")));
    }

    [Fact]
    public async Task Save_WhenTheCommitFailsAndTheRollbackAlsoFailsForAnExistingPage_ReportsRollbackFailedAndStillInvalidatesTheIndex()
    {
        // Reviewer blocker 1 (§6 block C2 remediation): a restore that itself fails must not leave the
        // index un-invalidated. Real failure injection, not a mock: a pre-commit hook that fails the
        // commit AND, as a side effect before exiting, makes docs/ unwritable -- so the write (already
        // landed via File.Move before the commit ever ran) is unaffected, but the rollback's
        // `git checkout -- <path>` genuinely cannot unlink the dirty file to restore it (verified
        // independently by hand before writing this test: chmod 555 on the containing directory makes
        // `git checkout --` fail with "unable to unlink old <path>: Permission denied").
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "original content");
        await InstallFailingPreCommitHookThatLocksDocsAsync();

        var service = CreateService(out var index);
        try
        {
            var result = await service.SaveAsync(
                new RouteValue("page"),
                "this will never be committed, and the rollback that would restore 'original content' " +
                "will itself fail",
                PageBaseRevision.ForBlob(sha),
                Author(),
                CancellationToken.None);

            Assert.Equal(SaveOutcome.RollbackFailed, result.Outcome);

            // Invalidation must not be conditional on the restore succeeding -- the substance of the fix.
            Assert.Same(PageIndexSnapshot.Empty, index.Current);

            // The write lock must still be released despite the rollback throwing -- proven by
            // re-acquiring it immediately, bounded, and succeeding well within the bound.
            using var reacquired = await RepositoryWriteLock.AcquireAsync(Paths.LockFilePath, TimeSpan.FromSeconds(5));
        }
        finally
        {
            MakeDocsWritableAgain();
        }
    }

    [Fact]
    public async Task Save_WhenTheCommitFailsAndTheRollbackAlsoFailsForABrandNewPage_ReportsRollbackFailedAndStillInvalidatesTheIndex()
    {
        // The other branch RollBackFailedSaveAsync has: no page existed at the declared base, so the
        // rollback is a plain File.Delete rather than a git checkout -- and deleting from a directory
        // with no write permission throws UnauthorizedAccessException for the same underlying reason
        // (verified independently by hand: `rm` under a chmod 555 directory fails "Permission denied").
        await InitializeRepositoryAsync();
        await InstallFailingPreCommitHookThatLocksDocsAsync();

        var service = CreateService(out var index);
        try
        {
            var result = await service.SaveAsync(
                new RouteValue("brandnew"),
                "this will never be committed, and the file it wrote can never be deleted either",
                PageBaseRevision.AbsentAtHead,
                Author(),
                CancellationToken.None);

            Assert.Equal(SaveOutcome.RollbackFailed, result.Outcome);
            Assert.Same(PageIndexSnapshot.Empty, index.Current);

            using var reacquired = await RepositoryWriteLock.AcquireAsync(Paths.LockFilePath, TimeSpan.FromSeconds(5));
        }
        finally
        {
            MakeDocsWritableAgain();
        }
    }

    [Fact]
    public async Task Save_WhenCommitFailsForAnExistingPage_InvalidatesOnlyAfterTheFileIsAlreadyRestored()
    {
        // D17's restore-then-invalidate ordering, proved through the real SaveAsync path via a
        // substitutable IPageIndex (mirroring IPageIndexBuilder's own reason for existing) rather than
        // by reasoning about the try/finally shape alone.
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "original content");
        await InstallFailingPreCommitHookAsync();

        var observingIndex = new OrderObservingIndex(Path.Combine(WorkingTree, "page.md"), "original content");
        var service = CreateService(observingIndex);

        var result = await service.SaveAsync(
            new RouteValue("page"),
            "this will never be committed",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.True(observingIndex.InvalidateWasCalled);
        Assert.True(
            observingIndex.FileWasAlreadyRestoredWhenInvalidateWasCalled,
            "Invalidate() ran before the working tree had actually been restored to its committed content.");
    }

    private async Task InitializeRepositoryAsync()
    {
        var service = new ContentRepositoryService(
            Paths,
            _git,
            new GitHookInstaller(_git),
            NullLogger<ContentRepositoryService>.Instance,
            Options.Create(new ContentStorageOptions { DataRoot = _dataRoot }));

        await service.EnsureRepositoryAsync();
    }

    private async Task<string> CommitPageDirectlyAsync(string relativePath, string content)
    {
        var full = Path.Combine(WorkingTree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var repositoryRelativePath = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", repositoryRelativePath]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "seed"],
            new GitAuthor("Seed", "seed@zerowiki.example").ToEnvironmentVariables());

        return await ReadHeadBlobShaAsync(repositoryRelativePath);
    }

    private async Task<string> ReadHeadBlobShaAsync(string repositoryRelativePath)
    {
        var result = await _git.RunOrThrowAsync(RepositoryRoot, ["rev-parse", $"HEAD:{repositoryRelativePath}"]);
        return result.StandardOutput.Trim();
    }

    private async Task<int> RevisionCountAsync()
    {
        var result = await _git.RunOrThrowAsync(RepositoryRoot, ["rev-list", "--count", "HEAD"]);
        return int.Parse(result.StandardOutput.Trim());
    }

    private async Task AssertPorcelainIsEmptyAsync()
    {
        var status = await _git.RunOrThrowAsync(RepositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }

    /// <summary>
    /// A hook this class never installs itself (only <c>pre-receive</c>/<c>post-receive</c>, D3) —
    /// written directly to give the named <i>Failed commit rolls back the write</i> scenario a
    /// deterministic way to make <c>git commit</c> fail without depending on a genuine system fault.
    /// </summary>
    private async Task InstallFailingPreCommitHookAsync()
    {
        var hooksDirectory = Path.Combine(RepositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, "#!/bin/sh\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Like <see cref="InstallFailingPreCommitHookAsync"/>, but the hook also removes write permission
    /// from <c>docs/</c> as a side effect before exiting 1 — real failure injection for the rollback
    /// step itself, timed so it lands strictly after the write (already on disk via <c>File.Move</c>
    /// before <c>git commit</c> ever runs) and strictly before <see cref="PageSaveService"/>'s own
    /// rollback attempt (which only runs once the commit has already failed).
    /// </summary>
    private async Task InstallFailingPreCommitHookThatLocksDocsAsync()
    {
        var hooksDirectory = Path.Combine(RepositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, $"#!/bin/sh\nchmod 555 '{WorkingTree}'\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// A <c>pre-commit</c> hook that touches <paramref name="startedMarkerPath"/> the instant it starts,
    /// then sleeps for <paramref name="sleepSeconds"/> before exiting 0 -- a synchronization point for
    /// tests that need to act while a save is provably deep inside its own <c>git commit</c> subprocess,
    /// without depending on a guessed delay.
    /// </summary>
    private async Task InstallHookThatSignalsThenSleepsAsync(string startedMarkerPath, int sleepSeconds)
    {
        var hooksDirectory = Path.Combine(RepositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, $"#!/bin/sh\ntouch '{startedMarkerPath}'\nsleep {sleepSeconds}\nexit 0\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"'{path}' never appeared.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Undoes <see cref="InstallFailingPreCommitHookThatLocksDocsAsync"/>'s chmod, so <see cref="Dispose"/>'s
    /// recursive delete can actually remove the directory afterward.</summary>
    private void MakeDocsWritableAgain()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(WorkingTree))
        {
            File.SetUnixFileMode(
                WorkingTree,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static AuthenticatedAccount Author(string username = "alice") =>
        new(Guid.NewGuid(), username, IsAdministrator: false);

    /// <summary>
    /// A fake <see cref="IPageIndex"/> whose <see cref="Invalidate"/> records, at the exact moment it is
    /// called, whether <paramref name="absolutePath"/> already holds <paramref name="expectedRestoredContent"/>
    /// on disk — used to prove D17's restore-then-invalidate ordering through the real
    /// <see cref="PageSaveService.SaveAsync"/> path (§6 block C2 remediation): a reordering mutant that
    /// calls <c>Invalidate()</c> before the restore produces the exact same end state in a single-threaded
    /// test (the file ends up restored either way), so only checking the file's content <em>at the moment
    /// <c>Invalidate()</c> runs</em> can tell the orders apart.
    /// </summary>
    private sealed class OrderObservingIndex(string absolutePath, string expectedRestoredContent) : IPageIndex
    {
        public PageIndexSnapshot Current => PageIndexSnapshot.Empty;

        public bool InvalidateWasCalled { get; private set; }

        public bool FileWasAlreadyRestoredWhenInvalidateWasCalled { get; private set; }

        public void Invalidate()
        {
            InvalidateWasCalled = true;
            FileWasAlreadyRestoredWhenInvalidateWasCalled =
                File.Exists(absolutePath) && File.ReadAllText(absolutePath) == expectedRestoredContent;
        }
    }

    private PageSaveService CreateService(out PageIndex index, TimeSpan? saveWriteLockTimeout = null)
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);
        var builder = new PageIndexBuilder(
            paths,
            _git,
            new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance),
            new PageFrontmatterExtractor(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser()),
            historyService,
            NullLogger<PageIndexBuilder>.Instance);
        index = new PageIndex(builder, NullLogger<PageIndex>.Instance);

        return CreateService(index, historyService, saveWriteLockTimeout);
    }

    private PageSaveService CreateService(IPageIndex index, TimeSpan? saveWriteLockTimeout = null)
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);

        return CreateService(index, historyService, saveWriteLockTimeout);
    }

    private PageSaveService CreateService(IPageIndex index, PageHistoryService historyService, TimeSpan? saveWriteLockTimeout)
    {
        var paths = Paths;
        var authorFactory = new AccountGitAuthorFactory(Options.Create(new ContentAuthorshipOptions()));

        return new PageSaveService(
            paths,
            _git,
            authorFactory,
            index,
            historyService,
            NullLogger<PageSaveService>.Instance,
            Options.Create(new ContentStorageOptions
            {
                DataRoot = _dataRoot,
                SaveWriteLockTimeout = saveWriteLockTimeout ?? TimeSpan.FromSeconds(10),
            }));
    }
}
