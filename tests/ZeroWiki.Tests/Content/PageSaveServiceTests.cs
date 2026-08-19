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
    private string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-save-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();

    // §6 remediation round three: set only by EnsureCaseSensitiveDataRootAsync, when it forces
    // _dataRoot onto a scratch case-sensitive APFS volume. Torn down in Dispose.
    private string? _forcedCaseSensitiveVolumeMountPoint;
    private string? _forcedCaseSensitiveVolumeImagePath;

    private ContentPaths Paths => new(_dataRoot);
    private string RepositoryRoot => Paths.RepositoryRoot;
    private string WorkingTree => Paths.WorkingTree;

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // _dataRoot may sit on a volume already unmounted below by the time this runs on some
                // orderings; the volume detach that follows is what actually reclaims the space.
            }
        }

        if (_forcedCaseSensitiveVolumeMountPoint is not null)
        {
            RunToolBestEffort("hdiutil", ["detach", _forcedCaseSensitiveVolumeMountPoint, "-force"]);
        }

        if (_forcedCaseSensitiveVolumeImagePath is not null && File.Exists(_forcedCaseSensitiveVolumeImagePath))
        {
            File.Delete(_forcedCaseSensitiveVolumeImagePath);
        }
    }

    /// <summary>
    /// §6 remediation round three. Repoints <see cref="_dataRoot"/> at a genuinely case-sensitive
    /// filesystem, using this section's proven technique -- a scratch case-sensitive APFS volume via
    /// <c>hdiutil</c> -- on macOS, whose default filesystem folds case. Not needed off macOS: this
    /// project's Linux deployment target, and Linux CI, are case-sensitive by default already (ext4), so
    /// an ordinary temp directory already lets two differently-cased entries coexist on disk. Forcing the
    /// volume on macOS is what turns "does the guard behave on some case-sensitive host or other" into a
    /// test that actually runs, rather than one that would need a Linux box to mean anything. Must be
    /// called before <see cref="InitializeRepositoryAsync"/> -- it replaces <see cref="_dataRoot"/>
    /// before the repository is ever created there.
    /// </summary>
    private async Task EnsureCaseSensitiveDataRootAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var imagePath = Path.Combine(Path.GetTempPath(), $"zerowiki-case-sensitive-{Guid.NewGuid():n}.dmg");
        var volumeName = $"zwcs{Guid.NewGuid():n}"[..16];

        await RunToolOrThrowAsync(
            "hdiutil",
            ["create", "-size", "64m", "-fs", "Case-sensitive APFS", "-volname", volumeName, imagePath]);
        var attachOutput = await RunToolOrThrowAsync("hdiutil", ["attach", imagePath, "-nobrowse"]);

        var mountPoint = attachOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length > 0)
            .Select(fields => fields[^1].Trim())
            .LastOrDefault(field => field.StartsWith("/Volumes/", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"hdiutil attach did not report a mount point:\n{attachOutput}");

        _forcedCaseSensitiveVolumeMountPoint = mountPoint;
        _forcedCaseSensitiveVolumeImagePath = imagePath;
        _dataRoot = Path.Combine(mountPoint, "data");
        Directory.CreateDirectory(_dataRoot);
    }

    private static async Task<string> RunToolOrThrowAsync(string fileName, string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}:\n{stdout}\n{stderr}");
        }

        return stdout;
    }

    /// <summary>Best-effort cleanup only -- a failed detach/delete must never fail a test that has
    /// already passed or failed on its own merits.</summary>
    private static void RunToolBestEffort(string fileName, string[] arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(fileName)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            process.WaitForExit(10_000);
        }
        catch
        {
            // Best-effort cleanup only; see the doc comment above.
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
    public async Task Save_ContentWithCrlfLineEndings_IsWrittenAsLfOnDisk()
    {
        // D17, §6 block D4 continuation round three: an HTML <textarea> submits CRLF regardless of
        // what the member typed, and this is the one place left that can guarantee LF-only storage now
        // that core.autocrlf is pinned to false on the content repository (ContentRepositoryService).
        await InitializeRepositoryAsync();
        var service = CreateService(out _);

        var result = await service.SaveAsync(
            new RouteValue("crlf-page"),
            "line one\r\nline two\r\nline three",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var written = await File.ReadAllTextAsync(Path.Combine(WorkingTree, "crlf-page.md"));
        Assert.Equal("line one\nline two\nline three", written);
        Assert.DoesNotContain('\r', written);
    }

    [Fact]
    public async Task Save_ContentWithALoneCarriageReturn_IsAlsoNormalizedToLf()
    {
        // A lone \r (old Mac-style) is normalized too, not only \r\n -- the goal is "every line ending
        // this repository ever stores is LF", not merely "reproduce what core.autocrlf=input used to
        // do" (which never touched a lone \r either). See PageSaveService.NormalizeLineEndings's own
        // remarks for the full reasoning.
        await InitializeRepositoryAsync();
        var service = CreateService(out _);

        var result = await service.SaveAsync(
            new RouteValue("cr-page"),
            "old mac style\rline two",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var written = await File.ReadAllTextAsync(Path.Combine(WorkingTree, "cr-page.md"));
        Assert.Equal("old mac style\nline two", written);
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
    public async Task Save_WhenGitsIndexBlindsStagingViaAssumeUnchanged_RollsBackRatherThanReportingSavedForLostContent()
    {
        // §6 remediation round two, Fix A -- reproduced red first through this exact call, not raw git,
        // before the fix existed: `git update-index --assume-unchanged` makes `git add`/`git status`
        // both silently pretend the path is unmodified. Round one's safety net asked `git status
        // --porcelain` -- the same index-backed bookkeeping -- and reported Saved off the empty result,
        // permanently losing the write (D9's own reconciliation uses the same two commands, so nothing
        // downstream ever noticed either). Confirmed independently by raw git before writing this test:
        //   git update-index --assume-unchanged docs/page.md ; write "MEMBER TEXT"
        //   git add -A                        -> stages nothing
        //   git status --porcelain -- <path>  -> ''
        //   HEAD blob: "Original body."   disk: "MEMBER TEXT"
        // After the fix: the content-level check (git hash-object vs. HEAD's blob) catches the
        // divergence and the save takes the existing rollback path instead of reporting Saved.
        await InitializeRepositoryAsync();
        var repositoryRelativePath = "docs/page.md";
        var sha = await CommitPageDirectlyAsync("page.md", "Original body.");
        await _git.RunOrThrowAsync(RepositoryRoot, ["update-index", "--assume-unchanged", repositoryRelativePath]);

        var service = CreateService(out var index);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "MEMBER TEXT",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal("Original body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
        Assert.Equal("Original body.", await ShowHeadBlobAsync(repositoryRelativePath));
        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public async Task Save_WhenAGitignoredBrandNewPageStagesNothing_RollsBackRatherThanReportingSaved()
    {
        // §6 remediation round two -- the other cause of "nothing staged" the brief named explicitly,
        // checked by execution rather than assumed. Finding: given this class's own `git add --
        // <single path>` (never `-A`), a genuinely new page a .gitignore rule matches never reaches
        // Fix A's content check at all -- `git add -- <explicit ignored path>` refuses outright (exit
        // 1, "The following paths are ignored...") the moment `add` runs, which the pre-existing
        // generic `catch (Exception ex)` around add/status/commit already routes to the ordinary
        // rollback-and-Failed path. `git add -A` (untested here; SaveAsync never calls it) is the
        // invocation that stages an ignored path silently instead of refusing -- this class's explicit
        // per-path `add` does not have that failure mode. Kept as a regression pinning the observed
        // (correct) outcome for this specific cause, not as a test of the new content-hash branch --
        // that branch's own coverage is the assume-unchanged test above, where "nothing staged" really
        // does arrive with a zero exit code. There is still no HEAD blob for a page that was never
        // committed, so IF a future git-invocation change ever made this scenario reach the content
        // check silently, "nothing staged for an AbsentAtHead page" remains unconditionally a fault
        // there too (probe.State != Blob skips the byte-identity branch entirely).
        await InitializeRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, ".gitignore"), "ignoredpage.md\n");
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", "docs/.gitignore"]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "seed gitignore"],
            new GitAuthor("Seed", "seed@zerowiki.example").ToEnvironmentVariables());

        var service = CreateService(out var index);
        var result = await service.SaveAsync(
            new RouteValue("ignoredpage"),
            "content nobody will ever see committed",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(WorkingTree, "ignoredpage.md")));
        Assert.Same(PageIndexSnapshot.Empty, index.Current);
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
    public async Task Save_ThroughAnAmbiguousAddress_IsRefusedAndWritesNothing()
    {
        // The witness D1/D17 name: "Chapter_1.md" (literal '_' doubles) and "Chapter  1.md" (two spaces,
        // each -> '_') both encode to the same route. Verified here, not merely asserted in a comment:
        Assert.Equal(
            PageRouteCodec.Encode("Chapter_1.md").Value,
            PageRouteCodec.Encode("Chapter  1.md").Value);
        Assert.Equal("Chapter__1", PageRouteCodec.Encode("Chapter_1.md").Value);

        await InitializeRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "Chapter_1.md"), "underscore file");
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "Chapter  1.md"), "two-space file");

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("Chapter__1"),
            "attempted save through the colliding address",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal("underscore file", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Chapter_1.md")));
        Assert.Equal("two-space file", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Chapter  1.md")));
    }

    [Fact]
    public async Task Save_AddressBecomesAmbiguousAfterTheContentWasLoaded_IsRefusedRatherThanApplied()
    {
        // The in-flight scenario (specs/content-editing/spec.md): a save was prepared against an address
        // that identified exactly one file, and a second file claiming that same address arrives -- an
        // Obsidian push landing "Chapter  1.md" -- before the save is applied. This must be reported
        // Refused, not Conflict: the declared base revision is still exactly current, so a check that only
        // re-compared base revisions would let this one through and silently write to "Chapter_1.md" while
        // "Chapter  1.md" became unaddressable.
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("Chapter_1.md", "v1");

        // The second claimant arrives by push, after the save's caller already loaded "Chapter_1.md" and
        // captured its base revision above.
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "Chapter  1.md"), "pushed by someone else");
        await _git.RunOrThrowAsync(RepositoryRoot, ["add", "docs/Chapter  1.md"]);
        await _git.RunOrThrowAsync(
            RepositoryRoot,
            ["commit", "-m", "pushed"],
            new GitAuthor("Pusher", "pusher@zerowiki.example").ToEnvironmentVariables());

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("Chapter__1"),
            "an edit prepared before the collision arrived",
            PageBaseRevision.ForBlob(sha),
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Chapter_1.md")));
    }

    [Fact]
    public async Task Save_RouteCaseDisagreesWithAnExistingFilesCaseOnThisHostsFilesystem_BehavesCorrectlyForIt()
    {
        // §6 remediation round two, Fix B -- the original version of this test pinned
        // `core.ignoreCase=true` via `git config`, but the guard it exercises
        // (ResolvedPathMatchesOnDiskCaseExactly) is gated on File.Exists/Directory.GetFileSystemEntries,
        // which answer from the *filesystem*, not from git's config: `core.ignoreCase` describes a
        // filesystem's behaviour to git, it does not configure the filesystem itself. Confirmed by
        // execution on a real case-sensitive volume (a scratch case-sensitive APFS volume, `hdiutil`)
        // with the old config pin left in place: the save correctly SUCCEEDED there as a legitimate new
        // page, proving the old hard-coded `Refused` assertion would fail on exactly this project's own
        // case-sensitive Linux deployment target. This version probes the host's actual case-folding
        // behaviour and asserts whichever outcome is correct for it, so it holds -- and actually
        // exercises the guard -- on both kinds of host rather than being skipped on one.
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync("Page.md", "Original body.");
        var isCaseInsensitiveHost = IsWorkingTreeFileSystemCaseInsensitive();

        var service = CreateService(out _);
        var result = await service.SaveAsync(
            new RouteValue("page"),
            "NEW TEXT FROM MEMBER",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        if (isCaseInsensitiveHost)
        {
            // D12's enumeration model computes a route strictly from each file's own literal name, so
            // "Page" and "page" are different routes by that model even though a case-insensitive
            // filesystem treats "Page.md" and "page.md" as the same physical file: enumeration
            // publishes only "Page", so route "page" reads as "no page here yet" (AbsentAtHead) right
            // up until an unguarded write would silently clobber the existing file. The guard refuses
            // before that write happens.
            Assert.Equal(SaveOutcome.Refused, result.Outcome);
            Assert.Equal("Original body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Page.md")));
            await AssertPorcelainIsEmptyAsync();
        }
        else
        {
            // On a case-sensitive filesystem "Page.md" and "page.md" genuinely coexist as two different,
            // non-colliding pages under D12's model -- there is nothing for the guard to disagree with,
            // and refusing here would be the false-refusal half of Blocker 1 the guard must not
            // reintroduce.
            Assert.Equal(SaveOutcome.Saved, result.Outcome);
            Assert.Equal("Original body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Page.md")));
            Assert.Equal("NEW TEXT FROM MEMBER", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
        }
    }

    [Fact]
    public async Task LoadForEdit_RouteCaseDisagreesWithAnExistingFilesCaseOnThisHostsFilesystem_BehavesCorrectlyForIt()
    {
        // The same disagreement one surface earlier (§6 remediation Blocker 1, made portable by round
        // two's Fix B -- see the Save_ sibling test's remarks for the full reasoning): before Blocker
        // 1's fix, /wiki/page offered "Create this page" for an already-existing docs/Page.md on a
        // case-insensitive filesystem, which is exactly what let a member's save believe it was
        // creating something new.
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync("Page.md", "Original body.");
        var isCaseInsensitiveHost = IsWorkingTreeFileSystemCaseInsensitive();

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue("page"), CancellationToken.None);

        if (isCaseInsensitiveHost)
        {
            Assert.Equal(PageLoadForEditOutcome.Refused, result.Outcome);
        }
        else
        {
            Assert.Equal(PageLoadForEditOutcome.New, result.Outcome);
        }
    }

    [Fact]
    public async Task Save_BothFileCasingsCoexistOnACaseSensitiveHost_UppercaseCommittedFirst_NeitherIsRefused()
    {
        // §6 remediation round three -- the defect this test targets: ResolvedPathMatchesOnDiskCaseExactly
        // used to take the FIRST case-insensitive match from Directory.GetFileSystemEntries and demand
        // that one be ordinally exact, rather than asking whether an exact match exists anywhere in the
        // listing. With "Page.md" and "page.md" both genuinely present -- legitimate, non-colliding D12
        // routes on a case-sensitive host -- readdir order (not either file's own correctness) decided
        // which route the guard refused. Forced onto a real case-sensitive volume (see
        // EnsureCaseSensitiveDataRootAsync) so this runs deterministically regardless of the host that
        // happens to execute the suite.
        await EnsureCaseSensitiveDataRootAsync();
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync("Page.md", "Upper case body.");

        var service = CreateService(out _);

        // The pre-existing entry must still resolve to itself once a second, differently-cased entry is
        // about to join it.
        var loadUpperBeforeSibling = await service.LoadForEditAsync(new RouteValue("Page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, loadUpperBeforeSibling.Outcome);
        Assert.Equal("Upper case body.", loadUpperBeforeSibling.Content);

        var saveLower = await service.SaveAsync(
            new RouteValue("page"),
            "Lower case body.",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, saveLower.Outcome);
        Assert.Equal("Upper case body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Page.md")));
        Assert.Equal("Lower case body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));

        // Both entries are now genuinely on disk together. Re-resolve BOTH routes -- not just the one
        // that was just written -- so whichever member readdir happens to enumerate first cannot decide
        // either outcome by luck: the surviving defect made exactly one direction fail, and which one
        // depended on hash order, not on which assertion this test happened to write.
        var reloadUpper = await service.LoadForEditAsync(new RouteValue("Page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadUpper.Outcome);
        Assert.Equal("Upper case body.", reloadUpper.Content);

        var reloadLower = await service.LoadForEditAsync(new RouteValue("page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadLower.Outcome);
        Assert.Equal("Lower case body.", reloadLower.Content);
    }

    [Fact]
    public async Task Save_BothFileCasingsCoexistOnACaseSensitiveHost_LowercaseCommittedFirst_NeitherIsRefused()
    {
        // The mirror of the sibling test above with the creation order reversed -- "which member loses"
        // was fixed by readdir order, which this codebase has no control over, so the only way to prove
        // order isn't deciding the outcome is to exercise both physical creation orders, not just one.
        await EnsureCaseSensitiveDataRootAsync();
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync("page.md", "Lower case body.");

        var service = CreateService(out _);

        var loadLowerBeforeSibling = await service.LoadForEditAsync(new RouteValue("page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, loadLowerBeforeSibling.Outcome);
        Assert.Equal("Lower case body.", loadLowerBeforeSibling.Content);

        var saveUpper = await service.SaveAsync(
            new RouteValue("Page"),
            "Upper case body.",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, saveUpper.Outcome);
        Assert.Equal("Lower case body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "page.md")));
        Assert.Equal("Upper case body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Page.md")));

        var reloadLower = await service.LoadForEditAsync(new RouteValue("page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadLower.Outcome);
        Assert.Equal("Lower case body.", reloadLower.Content);

        var reloadUpper = await service.LoadForEditAsync(new RouteValue("Page"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadUpper.Outcome);
        Assert.Equal("Upper case body.", reloadUpper.Content);
    }

    [Fact]
    public async Task Save_BothDirectoryCasingsCoexistOnACaseSensitiveHost_NeitherSubtreeIsBricked()
    {
        // The supervisor's specific concern: a directory pair (not just a file pair) takes its whole
        // subtree with it when the guard misfires, because ResolvedPathMatchesOnDiskCaseExactly walks
        // every path segment -- including intermediate directories -- and refuses the whole save the
        // moment any one segment disagrees. "Notes" and "notes" are legitimate, non-colliding directories
        // on a case-sensitive host, each with its own page underneath.
        await EnsureCaseSensitiveDataRootAsync();
        await InitializeRepositoryAsync();
        await CommitPageDirectlyAsync(Path.Combine("Notes", "apple.md"), "Apple body.");

        var service = CreateService(out _);

        var loadBeforeSibling = await service.LoadForEditAsync(new RouteValue("Notes/apple"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, loadBeforeSibling.Outcome);

        var saveInLowercaseDirectory = await service.SaveAsync(
            new RouteValue("notes/banana"),
            "Banana body.",
            PageBaseRevision.AbsentAtHead,
            Author(),
            CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, saveInLowercaseDirectory.Outcome);
        Assert.Equal("Apple body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "Notes", "apple.md")));
        Assert.Equal("Banana body.", await File.ReadAllTextAsync(Path.Combine(WorkingTree, "notes", "banana.md")));

        // Re-resolve both subtrees now that both directories genuinely coexist, in both directions, so
        // readdir order over the two directory entries cannot decide the outcome by luck either.
        var reloadUppercaseSubtree = await service.LoadForEditAsync(new RouteValue("Notes/apple"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadUppercaseSubtree.Outcome);
        Assert.Equal("Apple body.", reloadUppercaseSubtree.Content);

        var reloadLowercaseSubtree = await service.LoadForEditAsync(new RouteValue("notes/banana"), CancellationToken.None);
        Assert.Equal(PageLoadForEditOutcome.Found, reloadLowercaseSubtree.Outcome);
        Assert.Equal("Banana body.", reloadLowercaseSubtree.Content);
    }

    [Fact]
    public async Task LoadForEdit_ExistingPage_ReturnsItsContentAndTheBaseRevisionItCorrespondsTo()
    {
        await InitializeRepositoryAsync();
        var sha = await CommitPageDirectlyAsync("page.md", "hello");

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue("page"), CancellationToken.None);

        Assert.Equal(PageLoadForEditOutcome.Found, result.Outcome);
        Assert.Equal("hello", result.Content);
        Assert.Equal(sha, result.BaseRevision.BlobSha);
    }

    [Fact]
    public async Task LoadForEdit_AddressWithNoPageYet_ReturnsNewDeclaringAbsentAtHead()
    {
        await InitializeRepositoryAsync();

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue("brandnew"), CancellationToken.None);

        Assert.Equal(PageLoadForEditOutcome.New, result.Outcome);
        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(PageBaseRevision.AbsentAtHead, result.BaseRevision);
    }

    [Fact]
    public async Task LoadForEdit_AmbiguousAddress_IsRefusedRatherThanTreatedAsNoPageYet()
    {
        await InitializeRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "Chapter_1.md"), "underscore file");
        await File.WriteAllTextAsync(Path.Combine(WorkingTree, "Chapter  1.md"), "two-space file");

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue("Chapter__1"), CancellationToken.None);

        Assert.Equal(PageLoadForEditOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task LoadForEdit_NonCanonicalRouteValue_IsRefused()
    {
        await InitializeRepositoryAsync();

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue("a b"), CancellationToken.None);

        Assert.Equal(PageLoadForEditOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task LoadForEdit_UnresolvableRouteValue_IsRefused()
    {
        await InitializeRepositoryAsync();

        var service = CreateService(out _);
        var result = await service.LoadForEditAsync(new RouteValue(string.Empty), CancellationToken.None);

        Assert.Equal(PageLoadForEditOutcome.Refused, result.Outcome);
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

    private async Task<string> ShowHeadBlobAsync(string repositoryRelativePath)
    {
        var result = await _git.RunOrThrowAsync(RepositoryRoot, ["show", $"HEAD:{repositoryRelativePath}"]);
        return result.StandardOutput;
    }

    private async Task<int> RevisionCountAsync()
    {
        var result = await _git.RunOrThrowAsync(RepositoryRoot, ["rev-list", "--count", "HEAD"]);
        return int.Parse(result.StandardOutput.Trim());
    }

    /// <summary>
    /// §6 remediation round two, Fix B: probes <see cref="WorkingTree"/>'s actual case-folding
    /// behaviour by execution, rather than pinning <c>core.ignoreCase</c> -- which describes a
    /// filesystem's behaviour to git, it does not configure the filesystem itself. Must be called after
    /// <see cref="InitializeRepositoryAsync"/> has created <see cref="WorkingTree"/>, and probes that
    /// exact directory rather than <see cref="Path.GetTempPath"/> so the answer describes the volume
    /// the save under test actually writes to.
    /// </summary>
    private bool IsWorkingTreeFileSystemCaseInsensitive()
    {
        var probeName = $"case-probe-{Guid.NewGuid():n}";
        var probePath = Path.Combine(WorkingTree, probeName);
        File.WriteAllText(probePath, string.Empty);
        try
        {
            return File.Exists(Path.Combine(WorkingTree, probeName.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probePath);
        }
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
        var enumerationService = new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance);

        return new PageSaveService(
            paths,
            _git,
            authorFactory,
            index,
            historyService,
            enumerationService,
            NullLogger<PageSaveService>.Instance,
            Options.Create(new ContentStorageOptions
            {
                DataRoot = _dataRoot,
                SaveWriteLockTimeout = saveWriteLockTimeout ?? TimeSpan.FromSeconds(10),
            }));
    }
}
