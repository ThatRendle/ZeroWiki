using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public async Task StartupWriteLockHeldByAnotherProcess_RefusesToStartNamingTheLockFile()
    {
        // §5's supervisor review found AcquireStartupWriteLockAsync's fatal-refusal path (:238) had no
        // test at all — the only thing that would have pinned it is exactly this: a real second OS
        // process (LockHarnessProcess, the same fixture §5.1's own tests use) genuinely holding the
        // lock, so the app's own startup acquisition has no choice but to time out for real.
        Directory.CreateDirectory(_dataRoot);
        var lockFilePath = new ContentPaths(_dataRoot).LockFilePath;

        await using var holder = await LockHarnessProcess.StartHoldingAsync(lockFilePath, holdMs: 2000);

        var service = CreateService(writeLockTimeout: TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(lockFilePath, exception.Message, StringComparison.Ordinal);

        // The refusal happens before the repository is ever touched: AcquireStartupWriteLockAsync runs
        // before any Directory.CreateDirectory(repositoryRoot) or git invocation.
        Assert.False(Directory.Exists(RepositoryRoot));
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

    [Fact]
    public async Task Bootstrap_InstallsBothHooksExecutableAndLeavesTheTreeClean()
    {
        // Executable-bit hooks are a POSIX filesystem concept with no Windows equivalent, matching
        // GitHookInstaller's own PlatformNotSupportedException guard; nothing to assert on Windows.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var service = CreateService();

        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var preReceivePath = Path.Combine(repositoryRoot, ".git", "hooks", GitHookInstaller.PreReceiveHookName);
        var postReceivePath = Path.Combine(repositoryRoot, ".git", "hooks", GitHookInstaller.PostReceiveHookName);

        Assert.True(File.Exists(preReceivePath));
        Assert.True(File.Exists(postReceivePath));
        Assert.True(File.GetUnixFileMode(preReceivePath).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.GetUnixFileMode(postReceivePath).HasFlag(UnixFileMode.UserExecute));

        // .git/hooks sits outside the working tree (docs/), so installing hooks cannot itself dirty
        // the tree the working-tree-clean invariant governs.
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task HandEditedHook_IsRestoredOnTheNextStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var preReceivePath = Path.Combine(repositoryRoot, ".git", "hooks", GitHookInstaller.PreReceiveHookName);
        await File.WriteAllTextAsync(preReceivePath, "#!/bin/sh\necho 'hand-edited'\nexit 1\n");

        await service.EnsureRepositoryAsync();

        var restoredContent = await File.ReadAllTextAsync(preReceivePath);
        Assert.DoesNotContain("hand-edited", restoredContent, StringComparison.Ordinal);
        Assert.Contains("exit 0", restoredContent, StringComparison.Ordinal);
        Assert.True(File.GetUnixFileMode(preReceivePath).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task UntrackedFileOnADirtyTree_IsCommittedAsARecoveryCommitAuthoredBySystem()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "copied-in.md"), "# Copied in\n");

        await service.EnsureRepositoryAsync();

        Assert.Equal("2", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var authorName = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%an"])).StandardOutput.Trim();
        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("System", authorName);
        Assert.Equal("system@zerowiki.org", authorEmail);

        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "docs/copied-in.md"]);
        Assert.Equal("docs/copied-in.md", lsFiles.StandardOutput.Trim());

        // Task 3.3 (spec scenario "Untracked content is still reconciled, not refused"): read the
        // recovery commit's own tree, not just the current index — the two agree only because
        // AssertPorcelainIsEmptyAsync below happens to hold; this assertion does not depend on that.
        var committedContent = await _git.RunOrThrowAsync(repositoryRoot, ["show", "HEAD:docs/copied-in.md"]);
        Assert.Equal("# Copied in\n", committedContent.StandardOutput);

        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task ModifiedTrackedFile_IsCommittedAsARecoveryCommitAuthoredBySystem()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var gitKeepPath = Path.Combine(repositoryRoot, "docs", ".gitkeep");
        await File.WriteAllTextAsync(gitKeepPath, "no longer empty\n");

        await service.EnsureRepositoryAsync();

        Assert.Equal("2", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("system@zerowiki.org", authorEmail);

        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task CleanTree_ProducesNoRecoveryCommitOnRestart()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        await service.EnsureRepositoryAsync();

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task DocsDeletedBetweenStarts_FailsToStartNamingThePathAndCommitsNothing()
    {
        // Not a foreign repository — this is one ZeroWiki itself created, whose docs/ an operator then
        // deleted entirely (e.g. rm -rf docs) before the next restart. HEAD is born (the initial commit
        // exists), so this reaches the same "already has history" branch a foreign repository does, and
        // must refuse the same way. The alternative — falling through to reconciliation — would `git
        // add -A` the deletion of every page and commit it as a D9 "recovered" change, permanently
        // ratifying the accidental deletion into history rather than stopping it at the door.
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var commitCountBeforeDeletion = (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim();

        Directory.Delete(Path.Combine(repositoryRoot, "docs"), recursive: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(Path.Combine(repositoryRoot, "docs"), exception.Message, StringComparison.Ordinal);

        // The commit count must not have advanced — this is the assertion that actually matters here.
        // Without it, the test would pass even if the deletion had been silently committed first.
        Assert.Equal(
            commitCountBeforeDeletion,
            (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "docs")));
    }

    [Fact]
    public async Task NestedGitRepository_FailsToStartNamingThePathAndCommitsNothing()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var nestedPath = Path.Combine(repositoryRoot, "docs", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/copied-vault", exception.Message, StringComparison.Ordinal);

        // No commit was made, and the index carries no gitlink — `git reset` left it exactly as found.
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.DoesNotContain("160000", lsFiles.StandardOutput, StringComparison.Ordinal);

        // The offending content is untouched in the working tree — the operator can still fix it.
        Assert.True(File.Exists(Path.Combine(nestedPath, "note.md")));
    }

    [Fact]
    public async Task NestedGitRepositoryAsAGitfile_IsAlsoDetected()
    {
        // A directory whose .git is a gitfile (linked worktree layout) rather than an ordinary
        // directory — git stages this identically as a 160000 gitlink, so detection must not assume
        // .git is always a directory.
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;

        var realGitDirectory = Path.Combine(_dataRoot, "real-vault-gitdir");
        Directory.CreateDirectory(realGitDirectory);
        await _git.RunOrThrowAsync(realGitDirectory, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(realGitDirectory, "note.md"), "# Note\n");
        await _git.RunOrThrowAsync(realGitDirectory, ["add", "note.md"]);
        await _git.RunOrThrowAsync(
            realGitDirectory,
            ["commit", "-m", "inner commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });

        var nestedPath = Path.Combine(repositoryRoot, "docs", "copied-vault");
        Directory.CreateDirectory(nestedPath);
        await File.WriteAllTextAsync(
            Path.Combine(nestedPath, ".git"),
            $"gitdir: {Path.Combine(realGitDirectory, ".git")}\n");
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "note.md"), "# Note\n");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/copied-vault", exception.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.DoesNotContain("160000", lsFiles.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedGitRepository_IsDetectedRegardlessOfNestingDepth()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var nestedPath = Path.Combine(repositoryRoot, "docs", "a", "b", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/a/b/copied-vault", exception.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.DoesNotContain("160000", lsFiles.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryWhoseHeadAlreadyContainsAGitlink_StartsSuccessfully()
    {
        // Stands in for a gitlink that landed in history before this guard existed (no shipped build
        // has ever created one, but the guard must not brick a repository that already has one from
        // some other means). The check must ask "would committing now introduce a gitlink not already
        // in HEAD", not census the whole index — otherwise this would refuse to start forever.
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var nestedPath = Path.Combine(repositoryRoot, "docs", "historical-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        // Commit the gitlink directly, bypassing ReconcileWorkingTreeAsync's guard entirely, standing
        // in for a build that landed one before this check existed.
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "historical gitlink predating the guard"],
            GitAuthor.System.ToEnvironmentVariables());

        var gitlinkShaBeforeAdvance = ExtractGitlinkSha(
            (await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"])).StandardOutput);
        Assert.NotNull(gitlinkShaBeforeAdvance);

        // Advance the nested repository's own HEAD before restarting — the normal way a submodule (or
        // any legitimately adopted gitlink) changes over time. This is the shape the earlier version
        // of this test never reached: it produces an M record (old mode 160000, new mode 160000, a
        // changed SHA), not an A record or no record at all — and an M record's new mode is 160000 just
        // as readily as an A record's, which is exactly what bricked startup before the guard required
        // old mode to differ too.
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "another-note.md"), "# Another note\n");
        await _git.RunOrThrowAsync(nestedPath, ["add", "another-note.md"]);
        await _git.RunOrThrowAsync(
            nestedPath,
            ["commit", "-m", "nested repository advances its own HEAD"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });

        // The restart must succeed rather than refuse — the gitlink was already in HEAD, and its
        // nested repository merely advancing is not this reconciliation introducing anything.
        await service.EnsureRepositoryAsync();

        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        var gitlinkShaAfterAdvance = ExtractGitlinkSha(
            (await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"])).StandardOutput);
        Assert.NotEqual(gitlinkShaBeforeAdvance, gitlinkShaAfterAdvance);
    }

    [Fact]
    public async Task NestedGitRepositoryWithANonAsciiName_MessageContainsTheRealPathNotAnEscapedForm()
    {
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var nestedPath = Path.Combine(repositoryRoot, "docs", "café-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());

        Assert.Contains("docs/café-vault", exception.Message, StringComparison.Ordinal);

        // core.quotePath's default C-style octal escaping (e.g. "docs/caf\303\251-vault") must not
        // appear — that is the exact defect being fixed.
        Assert.DoesNotContain("\\303\\251", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"docs/", exception.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    // Foreign repositories: ones ZeroWiki did not itself create. Every fixture above builds a
    // repository ZeroWiki initialized on an earlier call, or a ZeroWiki-shaped one
    // (RepositoryCreatedWithoutTheConfiguration_...). Adopting a repository built entirely outside
    // ZeroWiki — someone else's history, someone else's layout — is a distinct code path
    // (EnsureInitialCommitAsync's "already has history" branch) that none of those fixtures exercise,
    // and it is where both remediation blockers lived.

    [Fact]
    public async Task ForeignRepositoryWithHistoryAndDocs_StartsSuccessfullyWithoutAddingACommit()
    {
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: true);
        var commitCountBefore = (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim();

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.Equal(commitCountBefore, (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertConfigurationIsAppliedAsync(repositoryRoot);
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, "docs")));

        var authorEmail = (await _git.RunOrThrowAsync(repositoryRoot, ["log", "-1", "--format=%ae"])).StandardOutput.Trim();
        Assert.Equal("somebody@example.com", authorEmail);
    }

    [Fact]
    public async Task ForeignRepositoryWithHistoryAndNoDocs_FailsToStartNamingWhatIsMissing()
    {
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: false);

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(Path.Combine(repositoryRoot, "docs"), exception.Message, StringComparison.Ordinal);

        // Nothing was created and nothing was committed — the repository is exactly as adopted.
        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "docs")));
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task ForeignRepositoryWithAPreExistingGitlink_StartsSuccessfullyAndSurvivesItAdvancing()
    {
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: true);

        var nestedPath = Path.Combine(repositoryRoot, "docs", "vendored-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "a gitlink somebody else added"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Somebody Else",
                ["GIT_AUTHOR_EMAIL"] = "somebody@example.com",
                ["GIT_COMMITTER_NAME"] = "Somebody Else",
                ["GIT_COMMITTER_EMAIL"] = "somebody@example.com",
            });

        var lsFilesBefore = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.Contains("160000", lsFilesBefore.StandardOutput, StringComparison.Ordinal);
        var gitlinkShaBeforeAdvance = ExtractGitlinkSha(lsFilesBefore.StandardOutput);

        // First-ever start over a genuinely foreign repository (never touched by this service before)
        // must succeed — the static case.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        // Advance the nested repository, then restart again — the M-record case, on a repository
        // ZeroWiki never initialized itself, which is exactly the combination the remediation covers.
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "another-note.md"), "# Another note\n");
        await _git.RunOrThrowAsync(nestedPath, ["add", "another-note.md"]);
        await _git.RunOrThrowAsync(
            nestedPath,
            ["commit", "-m", "nested repository advances"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });

        await service.EnsureRepositoryAsync();
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        var gitlinkShaAfterAdvance = ExtractGitlinkSha(
            (await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"])).StandardOutput);
        Assert.NotEqual(gitlinkShaBeforeAdvance, gitlinkShaAfterAdvance);
    }

    [Fact]
    public async Task ForeignRepositoryWithPreExistingHooksAndNoDocs_RefusesLeavingTheHooksByteForByteUnchanged()
    {
        // Executable-bit hooks are a POSIX filesystem concept with no Windows equivalent, matching
        // GitHookInstaller's own PlatformNotSupportedException guard; nothing to assert on Windows.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: false);

        var hooksDirectory = Path.Combine(repositoryRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var preReceivePath = Path.Combine(hooksDirectory, GitHookInstaller.PreReceiveHookName);
        var postReceivePath = Path.Combine(hooksDirectory, GitHookInstaller.PostReceiveHookName);
        const string preReceiveBody = "#!/bin/sh\necho 'operator-owned pre-receive'\nexit 0\n";
        const string postReceiveBody = "#!/bin/sh\necho 'operator-owned post-receive'\nexit 0\n";
        await File.WriteAllTextAsync(preReceivePath, preReceiveBody);
        await File.WriteAllTextAsync(postReceivePath, postReceiveBody);
        var operatorMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(preReceivePath, operatorMode);
        File.SetUnixFileMode(postReceivePath, operatorMode);

        var service = CreateService();

        // The refusal itself already passed before this block's fix — asserting only this would prove
        // nothing. What matters is that nothing was written before it fired.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(Path.Combine(repositoryRoot, "docs"), exception.Message, StringComparison.Ordinal);

        Assert.Equal(preReceiveBody, await File.ReadAllTextAsync(preReceivePath));
        Assert.Equal(postReceiveBody, await File.ReadAllTextAsync(postReceivePath));
        Assert.Equal(operatorMode, File.GetUnixFileMode(preReceivePath));
        Assert.Equal(operatorMode, File.GetUnixFileMode(postReceivePath));
    }

    // The pre-init filesystem scan: the initialise branch (RepositoryRoot has no .git of its own, and
    // is not a bare repository). Unlike every gitlink fixture above, these place the nested repository
    // *before* EnsureRepositoryAsync is ever called at all, standing in for a folder of Markdown (an
    // existing Obsidian vault, .git and all) copied onto the volume before ZeroWiki's first start. The
    // index-based gitlink check (FindStagedGitlinksAsync) cannot catch this in time: on this branch it
    // only ever runs after `git init` has already created a `.git` and an initial commit already
    // exists, so without the scan a `.git` and one commit would be left behind immediately before
    // refusing to serve.

    [Fact]
    public async Task NestedGitRepositoryOnAFreshVolume_RefusesBeforeGitInitEverRuns()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var nestedPath = Path.Combine(repositoryRoot, "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("copied-vault", exception.Message, StringComparison.Ordinal);

        // The whole point of this fixture: no .git was ever created at RepositoryRoot. The refusal
        // alone already passes without the scan (the index-based check would eventually catch this
        // too, just after git init and a commit had already run) — this is the assertion that proves
        // the scan ran before any write.
        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, ".git")));

        // The offending content itself is untouched — the operator can still fix it.
        Assert.True(File.Exists(Path.Combine(nestedPath, "note.md")));
    }

    [Fact]
    public async Task NestedGitRepositoryThreeLevelsDeepOnAFreshVolume_RefusesBeforeGitInitEverRuns()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var nestedPath = Path.Combine(repositoryRoot, "a", "b", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("a/b/copied-vault", exception.Message, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
    }

    [Fact]
    public async Task NestedGitRepositoryAsAGitfileOnAFreshVolume_RefusesBeforeGitInitEverRuns()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);

        var realGitDirectory = Path.Combine(_dataRoot, "real-vault-gitdir");
        Directory.CreateDirectory(realGitDirectory);
        await _git.RunOrThrowAsync(realGitDirectory, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(realGitDirectory, "note.md"), "# Note\n");
        await _git.RunOrThrowAsync(realGitDirectory, ["add", "note.md"]);
        await _git.RunOrThrowAsync(
            realGitDirectory,
            ["commit", "-m", "inner commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });

        var nestedPath = Path.Combine(repositoryRoot, "copied-vault");
        Directory.CreateDirectory(nestedPath);
        await File.WriteAllTextAsync(
            Path.Combine(nestedPath, ".git"),
            $"gitdir: {Path.Combine(realGitDirectory, ".git")}\n");
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "note.md"), "# Note\n");

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("copied-vault", exception.Message, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
    }

    [Fact]
    public async Task GitignoredNestedGitRepositoryOnAFreshVolume_IsStillRefusedByTheScan()
    {
        // The scan and the index-based gitlink check deliberately disagree here (Product Owner
        // decision, design.md D9 addendum): `git add -A` respects .gitignore, so a gitignored nested
        // repository is invisible to the index check — demonstrated directly below, standing in for
        // what that check alone would see. The filesystem scan does not consult .gitignore at all, so
        // it still finds the .git entry and refuses. This test pins that divergence as deliberate.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".gitignore"), "copied-vault/\n");
        var nestedPath = Path.Combine(repositoryRoot, "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        // The index check's side of the divergence: were this folder staged as-is (as if the scan
        // didn't exist), `.gitignore` hides the nested repository from `git add -A` entirely — no
        // gitlink, no trace of it in the index at all.
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        var lsFilesIgnoringTheScan = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.DoesNotContain("160000", lsFilesIgnoringTheScan.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("copied-vault", lsFilesIgnoringTheScan.StandardOutput, StringComparison.Ordinal);

        // Undo that probe so the real initialise path (scan included) is exercised exactly as an
        // operator would hit it — a fresh, .git-less RepositoryRoot.
        Directory.Delete(Path.Combine(repositoryRoot, ".git"), recursive: true);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("copied-vault", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
    }

    [Fact]
    public async Task NestedGitRepositoryBehindAnUnreadableDirectory_RefusesRatherThanSilentlySkippingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            // chmod-based unreadability is a POSIX permissions concept; the container image (this
            // scan's actual deployment target) is Linux, matching the existing Windows-skip precedent
            // for the executable-bit hook tests elsewhere in this file.
            return;
        }

        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var lockedDirectory = Path.Combine(repositoryRoot, "locked");
        Directory.CreateDirectory(lockedDirectory);
        var nestedPath = Path.Combine(lockedDirectory, "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        File.SetUnixFileMode(lockedDirectory, UnixFileMode.None);
        try
        {
            // chmod 000 does not block root. If this process can still enumerate the directory despite
            // the mode, it is running as root, the unreadable-directory case this test exists to
            // exercise never actually held, and asserting the refusal below would pass for the wrong
            // reason (root would see straight through to the nested repository and the ordinary gitlink
            // path would fire instead). Skip rather than let that happen silently.
            if (CanEnumerate(lockedDirectory))
            {
                return;
            }

            // The case this test actually exercises, given the check above did not skip: a genuinely
            // unreadable directory, confirmed by this process failing to list it.
            var service = CreateService();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());

            Assert.Contains("locked", exception.Message, StringComparison.Ordinal);

            // Distinct from the nested-repository message — this refusal is about permissions, not
            // about a repository to remove; the operator needs to know which action applies.
            Assert.DoesNotContain("copied-vault", exception.Message, StringComparison.Ordinal);

            // The whole point of this fixture: the unreadable subtree must stop bootstrap before
            // `git init` ever runs, exactly like a nested repository the scan can actually see.
            Assert.False(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        }
        finally
        {
            // Restore before Dispose() tries to recursively delete _dataRoot.
            File.SetUnixFileMode(
                lockedDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ReconciliationOfAnUnreadableDirectory_RefusesRatherThanCommittingTheReadableSubset()
    {
        // D17/`specs/content-store/spec.md` "Reconciliation refuses when it could not read part of the
        // working tree": `git add -A` exits 0 while warning on stderr that it could not read a
        // directory, so an unreadable subtree would otherwise be silently unstaged, the recovery
        // commit would "succeed", and `git status --porcelain` would report clean. Unlike the two tests
        // above, this exercises an *adopted* repository's reconciliation path (a second
        // EnsureRepositoryAsync call, after commit history already exists) rather than the pre-init
        // filesystem scan — the scan does not run here at all (design.md D9 addendum: it guards only
        // "about to create the initial commit"), so this is reconciliation's own gap, not the scan's.
        if (OperatingSystem.IsWindows())
        {
            // chmod-based unreadability is a POSIX permissions concept; the container image (this
            // check's actual deployment target) is Linux, matching the existing Windows-skip precedent
            // elsewhere in this file.
            return;
        }

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var lockedDirectory = Path.Combine(repositoryRoot, "docs", "locked");
        Directory.CreateDirectory(lockedDirectory);
        await File.WriteAllTextAsync(Path.Combine(lockedDirectory, "hidden.md"), "# Hidden\n");

        File.SetUnixFileMode(lockedDirectory, UnixFileMode.None);
        try
        {
            // chmod 000 does not block root. If this process can still enumerate the directory despite
            // the mode, it is running as root and the unreadable-directory case this test exists to
            // exercise never actually held — `git add -A` would stage it normally instead of warning,
            // and asserting the refusal below would pass for the wrong reason. Skip rather than let
            // that happen silently.
            if (CanEnumerate(lockedDirectory))
            {
                return;
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());

            // Names what git reported, not a generic message.
            Assert.Contains("could not open directory", exception.Message, StringComparison.Ordinal);
            Assert.Contains("locked", exception.Message, StringComparison.Ordinal);

            // D17, §6 block D4 continuation round three: the refusal must still fire and still name
            // what git said for a genuine unreadable directory — this is the case the message existed
            // for in the first place — but must no longer claim, unconditionally, that every such
            // stderr means a permissions problem (an earlier version of this exception did, and was
            // wrong for the CRLF-warning case this round fixed at the source instead).
            Assert.DoesNotContain("fix the reported permissions", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not necessarily a permissions problem", exception.Message, StringComparison.Ordinal);

            // The whole point of this fixture: no recovery commit was made over the readable subset.
            // `git status --porcelain` itself reports clean here (nothing readable changed, and it
            // does not surface the unreadable directory on stdout either) — exactly the "every later
            // check reports success" trap D17 exists to close, which is why the refusal above, not a
            // porcelain check, is the only thing that catches this.
            Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        }
        finally
        {
            // Restore before Dispose() tries to recursively delete _dataRoot.
            File.SetUnixFileMode(
                lockedDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task RestartingOverACommittedCrlfFileUnderInheritedAutocrlfInput_StartsSuccessfully()
    {
        // The Product Owner's own reported production defect, every link reproduced rather than
        // reasoned about (D17, §6 block D4 continuation round three): an HTML <textarea> submits CRLF
        // regardless of what the member typed; `git add -A` then warns on stderr about the conversion
        // it would perform "the next time git touches" that file whenever the *host's* inherited git
        // configuration sets core.autocrlf=input (or true) and git's own index stat cache happens to be
        // cold for that path — which a fresh clone, a container restart, or a remounted volume all
        // produce; and ReconcileWorkingTreeAsync's own guard refuses to start on any such stderr. This
        // is the regression test for exactly that chain, run via a genuine second OS process
        // (ReconcileHarnessProcess) so the hostile GIT_CONFIG_GLOBAL this test pins never touches this
        // test host's own process-wide environment (see ReconcileHarnessProcess's own remarks for why
        // that matters under xUnit's default parallelism).
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;
        var crlfFilePath = Path.Combine(repositoryRoot, "docs", "scratch.md");
        await File.WriteAllTextAsync(crlfFilePath, "editing it \r\n\r\n user A edit");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/scratch.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "add a CRLF-bearing file"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "test@zerowiki.example",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@zerowiki.example",
            });

        var hostileGlobalConfigPath = Path.Combine(_dataRoot, "hostile-gitconfig-for-test");
        await File.WriteAllTextAsync(hostileGlobalConfigPath, "[core]\n\tautocrlf = input\n");

        // Simulates the container restart / cold-stat-cache condition without depending on actually
        // getting git's stat cache cold: a fresh, second EnsureRepositoryAsync call is what the harness
        // process performs, in its own process, over the same data root.
        var (succeeded, output) = await ReconcileHarnessProcess.RunAsync(_dataRoot, hostileGlobalConfigPath);

        Assert.True(succeeded, $"Restart under inherited core.autocrlf=input should succeed; harness reported: {output}");
        Assert.Equal("RECONCILED", output);
    }

    private static bool CanEnumerate(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).ToList();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task SymlinkedDirectoryThatLoops_DoesNotHangStartup()
    {
        if (OperatingSystem.IsWindows())
        {
            // Symlink creation needs elevation/developer mode on Windows; the container image (this
            // scan's actual deployment target) is Linux, matching the existing Windows-skip precedent
            // for the executable-bit hook tests elsewhere in this file.
            return;
        }

        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var loopDirectory = Path.Combine(repositoryRoot, "loop");
        Directory.CreateDirectory(loopDirectory);
        File.CreateSymbolicLink(Path.Combine(loopDirectory, "self"), loopDirectory);

        var service = CreateService();

        // Bounded rather than a bare await, so a regression fails this assertion instead of hanging the
        // whole suite. What this pins is only "startup terminates", not "the ReparsePoint skip is what
        // stops the loop": with that skip disabled directly, a self-referential symlink's recursion
        // still self-terminates on its own — an OS-level path-depth bound, observed around 16 levels on
        // this machine — well before this timeout would ever fire, so this test alone does not exercise
        // the skip's teeth. The skip stays regardless: it is still the correct, portable way to avoid
        // depending on that OS bound, and it is what keeps the scan from treating a symlinked
        // directory's target as nested content of its own (see SymlinkPointingToARepositoryOutsideTheVolume_IsNotReportedAsNested).
        var bootstrap = service.EnsureRepositoryAsync();
        var completed = await Task.WhenAny(bootstrap, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(bootstrap, completed);
        await bootstrap;

        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
    }

    [Fact]
    public async Task SymlinkPointingToARepositoryOutsideTheVolume_IsNotReportedAsNested()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideRepository = Path.Combine(Path.GetTempPath(), $"zerowiki-outside-repo-{Guid.NewGuid():n}");
        Directory.CreateDirectory(outsideRepository);
        try
        {
            await _git.RunOrThrowAsync(outsideRepository, ["init", "-b", "main"]);
            await File.WriteAllTextAsync(Path.Combine(outsideRepository, "note.md"), "# Note\n");
            await _git.RunOrThrowAsync(outsideRepository, ["add", "note.md"]);
            await _git.RunOrThrowAsync(
                outsideRepository,
                ["commit", "-m", "outside commit"],
                new Dictionary<string, string>
                {
                    ["GIT_AUTHOR_NAME"] = "Outside",
                    ["GIT_AUTHOR_EMAIL"] = "outside@example.com",
                    ["GIT_COMMITTER_NAME"] = "Outside",
                    ["GIT_COMMITTER_EMAIL"] = "outside@example.com",
                });

            var repositoryRoot = RepositoryRoot;
            Directory.CreateDirectory(repositoryRoot);
            File.CreateSymbolicLink(Path.Combine(repositoryRoot, "linked-elsewhere"), outsideRepository);

            var service = CreateService();
            await service.EnsureRepositoryAsync();

            Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
            await AssertConfigurationIsAppliedAsync(repositoryRoot);
            await AssertPorcelainIsEmptyAsync(repositoryRoot);
        }
        finally
        {
            Directory.Delete(outsideRepository, recursive: true);
        }
    }

    [Fact]
    public async Task OrdinaryMarkdownCopiedOntoAFreshVolumeWithNoNestedRepository_InitializesNormally()
    {
        // Regression: the scan must not false-positive on a plain folder of Markdown with no nested
        // repository, which is the ordinary way to populate a new ZeroWiki.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "hello.md"), "# Hello\n");

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        await AssertIsNonBareAsync(repositoryRoot);
        await AssertConfigurationIsAppliedAsync(repositoryRoot);
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "docs/hello.md"]);
        Assert.Equal("docs/hello.md", lsFiles.StandardOutput.Trim());
    }

    // The scan's gating predicate: "the repository has no commits yet", not "repositoryRoot has no
    // .git of its own". The two coincide on a fresh volume (every fixture above) but diverge whenever
    // .git exists with HEAD still unborn — most plausibly a process that died between `git init` and
    // the initial commit. Before this fix, that state took the "existing repository" branch, the scan
    // was never called, and EnsureInitialCommitAsync wrote docs/.gitkeep and committed it as System
    // before the index-based gitlink check ever got a chance to fire.

    [Fact]
    public async Task GitInitializedButUnbornHead_WithNestedGitRepository_RefusesWithoutCreatingACommit()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        // .git present, HEAD unborn: stands in for a process that died between `git init` and the
        // initial commit that should have followed it.
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        var nestedPath = Path.Combine(repositoryRoot, "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("copied-vault", exception.Message, StringComparison.Ordinal);

        // The whole point of this fixture: HEAD must still be unborn — no initial commit was created
        // around the nested repository before the refusal fired. The refusal alone already passes
        // without the fix (FindStagedGitlinksAsync would eventually have caught this too, just after
        // the initial commit had already landed), so this is the assertion that actually matters.
        var headProbe = await _git.RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "HEAD"]);
        Assert.False(headProbe.Succeeded);

        // The offending content itself is untouched — the operator can still fix it.
        Assert.True(File.Exists(Path.Combine(nestedPath, "note.md")));
    }

    [Fact]
    public async Task GitInitializedButUnbornHead_WithNestedGitRepositoryAsAGitfile_RefusesWithoutCreatingACommit()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);

        var realGitDirectory = Path.Combine(_dataRoot, "real-vault-gitdir");
        Directory.CreateDirectory(realGitDirectory);
        await _git.RunOrThrowAsync(realGitDirectory, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(realGitDirectory, "note.md"), "# Note\n");
        await _git.RunOrThrowAsync(realGitDirectory, ["add", "note.md"]);
        await _git.RunOrThrowAsync(
            realGitDirectory,
            ["commit", "-m", "inner commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });

        var nestedPath = Path.Combine(repositoryRoot, "copied-vault");
        Directory.CreateDirectory(nestedPath);
        await File.WriteAllTextAsync(
            Path.Combine(nestedPath, ".git"),
            $"gitdir: {Path.Combine(realGitDirectory, ".git")}\n");
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "note.md"), "# Note\n");

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("copied-vault", exception.Message, StringComparison.Ordinal);

        var headProbe = await _git.RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "HEAD"]);
        Assert.False(headProbe.Succeeded);
    }

    [Fact]
    public async Task GitInitializedButUnbornHead_WithNestedGitRepositoryMoreThanOneLevelDeep_RefusesWithoutCreatingACommit()
    {
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        var nestedPath = Path.Combine(repositoryRoot, "a", "b", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("a/b/copied-vault", exception.Message, StringComparison.Ordinal);

        var headProbe = await _git.RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "HEAD"]);
        Assert.False(headProbe.Succeeded);
    }

    [Fact]
    public async Task GitInitializedButUnbornHead_WithOrdinaryMarkdownAndNoNestedRepository_InitializesNormally()
    {
        // The crash-mid-init shape without any nested repository at all: this must not turn a
        // recoverable interrupted start into a permanent refusal. It also pins that the scan does not
        // mistake repositoryRoot's own top-level .git — which now exists before the scan ever runs, on
        // this branch — for a nested repository of its own.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "hello.md"), "# Hello\n");

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.True(Directory.Exists(Path.Combine(repositoryRoot, ".git")));
        await AssertIsNonBareAsync(repositoryRoot);
        await AssertConfigurationIsAppliedAsync(repositoryRoot);
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        // At least the initial commit (docs/.gitkeep) landed; hello.md, pre-existing outside what the
        // initial commit stages, is picked up by D9 reconciliation as a second commit — the same shape
        // as OrdinaryMarkdownCopiedOntoAFreshVolumeWithNoNestedRepository_InitializesNormally above.
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "docs/hello.md"]);
        Assert.Equal("docs/hello.md", lsFiles.StandardOutput.Trim());
    }

    [Fact]
    public async Task HeadIsADanglingSymbolicRef_RefusesRatherThanOrphaningRealHistory()
    {
        // D17's second clause: `git rev-parse --verify -q HEAD` exits 1 both for a genuinely fresh
        // repository (no ref exists anywhere) and for a HEAD symbolic ref pointing at a branch that
        // does not exist while real history sits on a *different* ref — e.g. a botched rename or a
        // lost ref file. The exit code alone cannot tell the two apart. Without the for-each-ref
        // probe added to close this, the dangling case is misread as "fresh" and
        // EnsureInitialCommitAsync creates a new orphan root commit on the phantom branch, silently
        // disconnecting the wiki's real history from everything ZeroWiki reads while the app starts
        // and serves correct-looking pages with every other check green.
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: true);

        var realHeadSha = (await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "refs/heads/main"]))
            .StandardOutput.Trim();

        // Simulates a botched rename / lost ref file: HEAD now points at a branch that was never
        // created, while refs/heads/main still holds the repository's real history, untouched.
        await _git.RunOrThrowAsync(repositoryRoot, ["symbolic-ref", "HEAD", "refs/heads/ghost"]);

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());

        Assert.Contains("ghost", exception.Message, StringComparison.Ordinal);

        // The assertion that actually gates the defect: refusing alone is only half of it. The real
        // history must still be exactly where it was — no orphan root commit created anywhere, and
        // refs/heads/main untouched by the refused attempt.
        Assert.Equal(
            realHeadSha,
            (await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "refs/heads/main"])).StandardOutput.Trim());
        Assert.Equal(
            "1",
            (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "refs/heads/main"])).StandardOutput.Trim());

        // Nothing created the branch HEAD was left pointing at either.
        var ghostProbe = await _git.RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "refs/heads/ghost"]);
        Assert.False(ghostProbe.Succeeded);
    }

    [Fact]
    public async Task ForeignRepositoryWithDenyCurrentBranchRefuseAndANewGitlink_RefusesLeavingTheConfigUnchanged()
    {
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: true);

        // An operator's own export policy, predating adoption by ZeroWiki — must survive a refused
        // start untouched, not be overwritten to updateInstead before the gitlink check gets to run.
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "receive.denyCurrentBranch", "refuse"]);

        var nestedPath = Path.Combine(repositoryRoot, "docs", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/copied-vault", exception.Message, StringComparison.Ordinal);

        Assert.Equal(
            "refuse",
            (await _git.RunOrThrowAsync(repositoryRoot, ["config", "receive.denyCurrentBranch"])).StandardOutput.Trim());

        // http.receivepack was never set by the operator either; it must still be entirely absent,
        // not written and then abandoned mid-refusal.
        var receivepackConfig = await _git.RunAsync(repositoryRoot, ["config", "--get", "http.receivepack"]);
        Assert.False(receivepackConfig.Succeeded);
    }

    // ── §2 (ignore-obsidian-config-in-content-repo): the permanent falsifiers ──────────────────

    [Fact]
    public async Task ObsidianConfigAtBothVaultLevels_IsNotStagedByUnscopedReconciliation()
    {
        // 2.1 (D3/D4): the falsifier drives ReconcileWorkingTreeAsync's own unscoped `add -A`, not
        // the .gitignore's text — a rule git does not actually apply would still pass a check that
        // only reads the file. D3 requires the rule to cover the vault opened at the repository
        // root *and* at docs/, so both are written here — a root-only fixture would leave D3 with
        // no falsifier at all.
        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var repositoryRoot = RepositoryRoot;

        var rootObsidian = Path.Combine(repositoryRoot, ".obsidian");
        Directory.CreateDirectory(rootObsidian);
        await File.WriteAllTextAsync(Path.Combine(rootObsidian, "workspace.json"), "{}");

        var docsObsidian = Path.Combine(repositoryRoot, "docs", ".obsidian");
        Directory.CreateDirectory(docsObsidian);
        await File.WriteAllTextAsync(Path.Combine(docsObsidian, "workspace.json"), "{}");

        // A second call re-runs ReconcileWorkingTreeAsync — it is unconditional on every start, not
        // only on the one that creates the initial commit — mirroring a running instance's next
        // reconciliation pass discovering what an editor just wrote into the working tree.
        await service.EnsureRepositoryAsync();

        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files"]);
        Assert.DoesNotContain(".obsidian", lsFiles.StandardOutput, StringComparison.Ordinal);

        // Nothing was staged, so reconciliation had nothing to commit (D9: a clean tree produces no
        // commit) — still exactly the one initial commit. If the rule were undone, `add -A` would
        // stage both files and this call would produce a second, recovery commit instead.
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);

        // §2 remediation: the assertions above are satisfied just as well by a hostile host's own
        // global excludesFile silently skipping the stage — ls-files/rev-list/porcelain don't consult
        // excludes, so on such a host they hold even with the seeding removed entirely (observed and
        // recorded in the DEVLOG). These two assertions require the code's *own* seeded rule to exist
        // and be committed, which no host configuration can manufacture on the code's behalf.
        Assert.Contains(".gitignore", lsFiles.StandardOutput, StringComparison.Ordinal);
        var committedGitignore = await _git.RunOrThrowAsync(repositoryRoot, ["show", "HEAD:.gitignore"]);
        Assert.Contains(
            committedGitignore.StandardOutput.Split('\n'),
            line => line.Trim() == ".obsidian/");
    }

    [Fact]
    public async Task ObsidianConfigAlreadyTrackedInHistory_StaysTrackedAndTreeStaysClean()
    {
        // 2.3 (D2): history is adopted as it stands. A repository that already carries .obsidian/ in
        // its committed history — e.g. one from before this feature existed — must never be
        // untracked; a `git rm --cached` slipping in anywhere turns this red.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", ".gitkeep"), string.Empty);
        var obsidianDir = Path.Combine(repositoryRoot, ".obsidian");
        Directory.CreateDirectory(obsidianDir);
        var obsidianFilePath = Path.Combine(obsidianDir, "workspace.json");
        await File.WriteAllTextAsync(obsidianFilePath, "{}");
        // -f (2.6): this fixture is deliberately building a repository whose history already tracks
        // .obsidian/, regardless of what a developer's or CI runner's own global excludes happen to
        // say about that path — plain `add` would refuse under a global excludesFile covering it.
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-f", "docs/.gitkeep", ".obsidian/workspace.json"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "pre-existing commit carrying .obsidian/"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Somebody Else",
                ["GIT_AUTHOR_EMAIL"] = "somebody@example.com",
                ["GIT_COMMITTER_NAME"] = "Somebody Else",
                ["GIT_COMMITTER_EMAIL"] = "somebody@example.com",
            });

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files"]);
        Assert.Contains(".obsidian/workspace.json", lsFiles.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(obsidianFilePath));

        // Same one commit as adopted — nothing was rewritten, and no gitignore was seeded either
        // (EnsureInitialCommitAsync's write branch never runs once the repository already has
        // history).
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        Assert.False(File.Exists(Path.Combine(repositoryRoot, ".gitignore")));
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task AdoptedRepositoryWithNoGitignore_GetsNoneSeededByZeroWiki()
    {
        // 2.4 (D1): seeding is bootstrap-only. A repository ZeroWiki adopts — already has commits —
        // never gets a .gitignore created for it, however long it goes without one; any seeding on
        // this branch turns this red.
        var repositoryRoot = RepositoryRoot;
        await CreateForeignRepositoryAsync(repositoryRoot, withDocsDirectory: true);
        Assert.False(File.Exists(Path.Combine(repositoryRoot, ".gitignore")));

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.False(File.Exists(Path.Combine(repositoryRoot, ".gitignore")));
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task PreExistingGitignoreWithAnUnrelatedRule_AppendsOursAndKeepsTheirs()
    {
        // 2.5, case 1. Also covers the 1.2 clean-tree gap the §1 supervisor found unevidenced: this
        // is the append branch, not the fresh-write one.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        await File.WriteAllTextAsync(gitIgnorePath, "*.tmp\n");

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.Equal("*.tmp\n.obsidian/\n", await File.ReadAllTextAsync(gitIgnorePath));

        // Functional proof, not just text (D4) — pinned per 2.6 (see PinnedGitEnvironment) so a
        // developer's own global excludes can't make this pass for the wrong reason.
        var checkIgnore = await _git.RunOrThrowAsync(
            repositoryRoot, ["check-ignore", "-q", ".obsidian/workspace.json"], PinnedGitEnvironment);
        Assert.Equal(0, checkIgnore.ExitCode);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task PreExistingGitignoreAlreadyCarryingTheExactRule_IsLeftByteIdentical()
    {
        // 2.5, case 2. Also covers the append-branch clean-tree gap (see previous test).
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        const string Seeded = "node_modules/\n.obsidian/\n*.log\n";
        await File.WriteAllTextAsync(gitIgnorePath, Seeded);

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.Equal(Seeded, await File.ReadAllTextAsync(gitIgnorePath));

        var checkIgnore = await _git.RunOrThrowAsync(
            repositoryRoot, ["check-ignore", "-q", ".obsidian/workspace.json"], PinnedGitEnvironment);
        Assert.Equal(0, checkIgnore.ExitCode);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task PreExistingGitignoreMentioningTheRuleOnlyInAComment_StillAppendsIt()
    {
        // 2.5, case 3 — the load-bearing one. A substring implementation matches ".obsidian/" inside
        // this comment and wrongly treats the rule as already present; only a whole trimmed line
        // counts (D5). Also covers the append-branch clean-tree gap (see the first 2.5 test).
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        const string Seeded = "# .obsidian/ is deliberately tracked in this repository\n";
        await File.WriteAllTextAsync(gitIgnorePath, Seeded);

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.Equal(Seeded + ".obsidian/\n", await File.ReadAllTextAsync(gitIgnorePath));

        var checkIgnore = await _git.RunOrThrowAsync(
            repositoryRoot, ["check-ignore", "-q", ".obsidian/workspace.json"], PinnedGitEnvironment);
        Assert.Equal(0, checkIgnore.ExitCode);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task PreExistingGitignoreWithNoTrailingNewline_GetsASeparatingNewlineBeforeOurRule()
    {
        // 2.5, case 4 — the sub-branch the §2 supervisor found unfalsified: needsSeparatingNewline
        // (ContentRepositoryService.cs) is false in every other 2.5 fixture because they all end in
        // "\n". Without this test a mutant hard-coding that flag to false survives the whole suite,
        // and the real defect it guards against is silent and damaging: appending directly onto an
        // operator's last line without a trailing newline folds our rule onto theirs, producing
        // "*.tmp.obsidian/" — their rule broken, ours never applied, no error anywhere.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        await File.WriteAllTextAsync(gitIgnorePath, "*.tmp");

        var service = CreateService();
        await service.EnsureRepositoryAsync();

        Assert.Equal("*.tmp\n.obsidian/\n", await File.ReadAllTextAsync(gitIgnorePath));

        var checkIgnoreTmp = await _git.RunOrThrowAsync(
            repositoryRoot, ["check-ignore", "-q", "foo.tmp"], PinnedGitEnvironment);
        Assert.Equal(0, checkIgnoreTmp.ExitCode);

        var checkIgnoreObsidian = await _git.RunOrThrowAsync(
            repositoryRoot, ["check-ignore", "-q", ".obsidian/workspace.json"], PinnedGitEnvironment);
        Assert.Equal(0, checkIgnoreObsidian.ExitCode);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Fact]
    public async Task PreExistingGitignoreMatchingGitignoreItself_RefusesToStartWithNoInitialCommit()
    {
        // 2.7 — the fail-fast Risks entry (Product Owner decision). A broad operator rule that also
        // matches .gitignore itself makes the explicit `git add docs/.gitkeep .gitignore` refuse
        // (git refuses to add an explicitly-named ignored path without -f, which this app never
        // passes), and the system must fail to start rather than complete an initial commit that is
        // missing the seeded rule.
        var repositoryRoot = RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".gitignore"), ".gitignore\n");

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<GitProcessException>(() => service.EnsureRepositoryAsync());
        Assert.Contains(".gitignore", exception.Message, StringComparison.Ordinal);

        // Not merely "something threw" — no initial commit exists at all. HEAD is still unborn, not
        // a commit that landed without the rule.
        var headProbe = await _git.RunAsync(repositoryRoot, ["rev-parse", "--verify", "-q", "HEAD"]);
        Assert.False(headProbe.Succeeded);
    }

    // ── fix-reconciliation-index-blindness §3: suppressed index entry regression coverage ──────
    //
    // Every fixture above builds its dirtiness through git's own comparisons (add/status), so none of
    // them can construct a path whose --assume-unchanged/--skip-worktree bit hides a divergence from
    // those very comparisons (Decision 6). SetSuppressedBitAsync below is that fixture; task 3.1's own
    // falsifier test verifies the fixture actually suppresses before anything downstream relies on it.

    public enum SuppressionKind
    {
        AssumeUnchanged,
        SkipWorktree,
    }

    /// <summary>3.1's fixture: sets the bit via a real <c>git update-index</c> call, never through code
    /// under test.</summary>
    private async Task SetSuppressedBitAsync(string repositoryRoot, string repositoryRelativePath, SuppressionKind kind)
    {
        var flag = kind == SuppressionKind.AssumeUnchanged ? "--assume-unchanged" : "--skip-worktree";
        await _git.RunOrThrowAsync(repositoryRoot, ["update-index", flag, "--", repositoryRelativePath]);
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged, "h")]
    [InlineData(SuppressionKind.SkipWorktree, "S")]
    public async Task SuppressedIndexBitFixture_HidesADivergenceFromGitsOwnInstruments(
        SuppressionKind kind, string expectedTag)
    {
        // Task 3.1's own falsifier: a fixture that silently failed to set the bit would make every
        // test below pass for the wrong reason, and nothing downstream would notice. Checked directly
        // against git's own output, not by trusting SetSuppressedBitAsync's exit code.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", kind);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", ".gitkeep"), "diverged after the bit was set\n");

        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "-v", "docs/.gitkeep"]);
        Assert.Equal($"{expectedTag} docs/.gitkeep", lsFiles.StandardOutput.Trim());

        var status = await _git.RunOrThrowAsync(repositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged, "--assume-unchanged")]
    [InlineData(SuppressionKind.SkipWorktree, "--skip-worktree")]
    public async Task SuppressedTrackedFileThatDiverges_RefusesNamingThePathAndTheIndexState(
        SuppressionKind kind, string expectedIndexStateName)
    {
        // Task 3.2, Decision 4 shape (a): both sides resolve and differ.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var gitKeepPath = Path.Combine(repositoryRoot, "docs", ".gitkeep");

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", kind);
        await File.WriteAllTextAsync(gitKeepPath, "diverged after the bit was set\n");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/.gitkeep", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedIndexStateName, exception.Message, StringComparison.Ordinal);

        // Nothing was committed past the divergence, and the bit is untouched (Decision 3: never
        // silently clear an operator's explicit instruction).
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var expectedTag = kind == SuppressionKind.AssumeUnchanged ? "h" : "S";
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "-v", "docs/.gitkeep"]);
        Assert.Equal($"{expectedTag} docs/.gitkeep", lsFiles.StandardOutput.Trim());
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged)]
    [InlineData(SuppressionKind.SkipWorktree)]
    public async Task SuppressedTrackedFileDeletedFromWorkingTree_RefusesAsAMissingFile(SuppressionKind kind)
    {
        // Task 3.2, Decision 4 shape (b): the working-tree file is absent — a suppressed deletion.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var gitKeepPath = Path.Combine(repositoryRoot, "docs", ".gitkeep");

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", kind);
        File.Delete(gitKeepPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/.gitkeep", exception.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    [Fact]
    public async Task SuppressedEntryStagedButNeverCommitted_RefusesAsNotInHead()
    {
        // Task 3.2, Decision 4 shape (c): the path is in the index but never made it into a commit
        // while suppressed, so it can never be staged or committed by reconciliation either.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;

        var newPagePath = Path.Combine(repositoryRoot, "docs", "uncommitted.md");
        await File.WriteAllTextAsync(newPagePath, "# Uncommitted\n");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/uncommitted.md"]);
        await SetSuppressedBitAsync(repositoryRoot, "docs/uncommitted.md", SuppressionKind.AssumeUnchanged);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/uncommitted.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--assume-unchanged", exception.Message, StringComparison.Ordinal);

        // The census runs before `add -A`, so the refusal fired before anything was staged or
        // committed past the one initial commit.
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged)]
    [InlineData(SuppressionKind.SkipWorktree)]
    public async Task SuppressedTrackedFileMatchingHead_StartsNormally(SuppressionKind kind)
    {
        // Task 3.2, Decision 4's harmless case: the invariant the bit protects is intact, so this must
        // not refuse — the falsifier for a guard keyed on the bit's presence rather than on divergence.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", kind);

        await service.EnsureRepositoryAsync();

        await AssertPorcelainIsEmptyAsync(repositoryRoot);
        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    [Fact]
    public async Task SuppressedEntryHarmlessAtReconciliationButDivergentAfterItsOwnCommit_RefusesAtTheSelfCheckAlone()
    {
        // Task 3.4 (Decision 5's own falsifier, construction from the section 2 supervisor — no test
        // seam needed, GitProcessRunner is sealed and non-virtual): stage different content into the
        // index, suppress the path (so add -A no longer looks at the working tree for it), then restore
        // the working tree to match what HEAD still holds. Reconciliation's census compares working
        // tree↔HEAD → Matches → it passes; add -A leaves the suppressed path alone; the index still
        // differs from HEAD, so the recovery commit lands, and HEAD's blob becomes the staged content.
        // Only then does the (untouched) working tree diverge from the new HEAD — caught only by
        // AssertWorkingTreeIsCleanAsync re-deriving its own census independently, which is exactly what
        // this test must die if 2.3 alone is reverted (self-tested below, not asserted from the code).
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var gitKeepPath = Path.Combine(repositoryRoot, "docs", ".gitkeep");

        await File.WriteAllTextAsync(gitKeepPath, "staged before suppression\n");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/.gitkeep"]);

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", SuppressionKind.AssumeUnchanged);

        // Restore the working tree to the original (still-HEAD) content. The index keeps the blob
        // staged above; only the working tree is reverted.
        await File.WriteAllTextAsync(gitKeepPath, string.Empty);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/.gitkeep", exception.Message, StringComparison.Ordinal);

        // Reconciliation itself did not refuse — it committed the staged content (D9's "always commit"
        // policy, applied to what its own census had just called harmless): a second commit exists, and
        // it is the staged content, not the original.
        Assert.Equal("2", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var committedContent = await _git.RunOrThrowAsync(repositoryRoot, ["show", "HEAD:docs/.gitkeep"]);
        Assert.Equal("staged before suppression\n", committedContent.StandardOutput);
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged)]
    [InlineData(SuppressionKind.SkipWorktree)]
    public async Task SuppressedAlreadyAdoptedGitlink_StartsNormally(SuppressionKind kind)
    {
        // Task 3.6, the gitlink shape section 1 got wrong. Ordering trap (section 2 supervisor): the
        // gitlink must already be in HEAD *before* this restart, and the nested repository must stay
        // clean — otherwise AssertWorkingTreeIsCleanAsync's `status --porcelain` refuses first, for a
        // reason unrelated to suppression, and this test would pass for the wrong cause.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;

        var nestedPath = Path.Combine(repositoryRoot, "docs", "adopted-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        // Commit the gitlink directly, bypassing ReconcileWorkingTreeAsync's own guard entirely —
        // standing in for a gitlink already adopted into HEAD before this restart.
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "gitlink already adopted before suppression"],
            GitAuthor.System.ToEnvironmentVariables());

        await SetSuppressedBitAsync(repositoryRoot, "docs/adopted-vault", kind);

        // Must succeed: index mode 160000, HEAD mode also 160000 — the harmless side of the
        // reconstructed gitlink condition (2.2), and the nested repository is untouched since the
        // commit above, so status --porcelain has nothing else to report.
        await service.EnsureRepositoryAsync();
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged)]
    [InlineData(SuppressionKind.SkipWorktree)]
    public async Task SuppressedUnmodifiedSymlink_StartsNormally(SuppressionKind kind)
    {
        // Task 3.6, the symlink shape section 1 got wrong (Blocker 1). The container's actual deployment
        // target is Linux; symlink creation on Windows needs elevation, matching the existing
        // Windows-skip precedent elsewhere in this file.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;

        var linkPath = Path.Combine(repositoryRoot, "docs", "link.md");
        File.CreateSymbolicLink(linkPath, "target.md");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/link.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "add a symlink before suppression"],
            GitAuthor.System.ToEnvironmentVariables());

        await SetSuppressedBitAsync(repositoryRoot, "docs/link.md", kind);

        // Nothing about the symlink changed since it was committed — the harmless shape Blocker 1
        // exists to protect.
        await service.EnsureRepositoryAsync();
        await AssertPorcelainIsEmptyAsync(repositoryRoot);
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged, "--assume-unchanged")]
    [InlineData(SuppressionKind.SkipWorktree, "--skip-worktree")]
    public async Task SuppressedSymlinkThatIsRepointed_RefusesNamingThePathAndTheIndexState(
        SuppressionKind kind, string expectedIndexStateName)
    {
        // Supervisor finding B1 (section 3 remediation): 3.6 pinned only the harmless direction of the
        // symlink dispatch (SuppressedUnmodifiedSymlink_StartsNormally, above) — this is the divergent
        // direction the spec's "SHALL refuse" actually names, and CompareSuppressedSymlinkToHeadAsync's
        // own remarks claimed it "confirmed" without a committed falsifier. Falsifier: blinding that
        // method to always return Matches must kill this test.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var linkPath = Path.Combine(repositoryRoot, "docs", "link.md");

        File.CreateSymbolicLink(linkPath, "target.md");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/link.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "add a symlink before suppression"],
            GitAuthor.System.ToEnvironmentVariables());

        await SetSuppressedBitAsync(repositoryRoot, "docs/link.md", kind);

        // Re-point the symlink's own link text after suppression — the working tree no longer agrees
        // with what HEAD recorded, and git's own instruments cannot see it because the bit hides them.
        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, "elsewhere.md");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/link.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedIndexStateName, exception.Message, StringComparison.Ordinal);

        // Nothing was committed past the divergence, and the bit is untouched (Decision 3).
        Assert.Equal("2", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var expectedTag = kind == SuppressionKind.AssumeUnchanged ? "h" : "S";
        var lsFiles = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "-v", "docs/link.md"]);
        Assert.Equal($"{expectedTag} docs/link.md", lsFiles.StandardOutput.Trim());
    }

    [Theory]
    [InlineData(SuppressionKind.AssumeUnchanged, "--assume-unchanged")]
    [InlineData(SuppressionKind.SkipWorktree, "--skip-worktree")]
    public async Task SuppressedTrackedFileReplacedByAGitlink_Refuses(
        SuppressionKind kind, string expectedIndexStateName)
    {
        // Supervisor finding B2 (section 3 remediation): 3.6 pinned only the harmless half of the
        // NotCompared arm (an already-adopted gitlink whose nested HEAD merely advanced) — this is
        // Decision 7's "a tracked file replaced by a gitlink" typechange, the fault half of the same
        // condition. Falsifier: forcing IsSuppressedEntryFault's NotCompared arm to false must kill
        // this test.
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var placeholderPath = Path.Combine(repositoryRoot, "docs", "vault-placeholder.md");

        // HEAD holds this path as an ordinary blob, committed directly (bypassing the service).
        await File.WriteAllTextAsync(placeholderPath, "# Placeholder\n");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/vault-placeholder.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "a tracked file, before it is replaced by a gitlink"],
            GitAuthor.System.ToEnvironmentVariables());

        // Replace the tracked file with a nested git repository of the same name, and stage it —
        // bypassing ReconcileWorkingTreeAsync's own `add -A`/gitlink guard, which the census (running
        // before that guard, Decision 6) must catch on its own. Never committed: HEAD keeps the blob,
        // so the index's 160000 mode disagrees with HEAD's, which is the shape under test.
        File.Delete(placeholderPath);
        await CreateNestedGitRepositoryDirectoryAsync(placeholderPath);
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/vault-placeholder.md"]);

        var lsFilesStage = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage", "docs/vault-placeholder.md"]);
        Assert.StartsWith("160000 ", lsFilesStage.StandardOutput);

        await SetSuppressedBitAsync(repositoryRoot, "docs/vault-placeholder.md", kind);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/vault-placeholder.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedIndexStateName, exception.Message, StringComparison.Ordinal);

        // Nothing was committed past the fault — the census refuses before `add -A` even runs
        // (Decision 6), so the gitlink never reaches HEAD.
        Assert.Equal("2", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var lsFilesAfter = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage", "docs/vault-placeholder.md"]);
        Assert.StartsWith("160000 ", lsFilesAfter.StandardOutput);
    }

    [Fact]
    public async Task SuppressedDivergenceAndANestedRepository_TheSuppressedEntryRefusesFirstAndTheGitlinkFaultIsDeferred()
    {
        // Task 3.9. Every audit in this change (worker, reviewer, supervisor) constructed its fault
        // shapes alone; this builds two together in one working tree — a suppressed, divergent tracked
        // file, and an untracked nested git repository not yet a gitlink in HEAD. The doc comment at
        // ContentRepositoryService.cs:809-812 claims neither refusal "can swallow" the other; this pins
        // which one actually wins on a single restart, and that the deferred one still fires on the
        // very next restart, so a reader does not mistake "wins first" for "the other is lost".
        var service = CreateService();
        await service.EnsureRepositoryAsync();
        var repositoryRoot = RepositoryRoot;
        var gitKeepPath = Path.Combine(repositoryRoot, "docs", ".gitkeep");

        await SetSuppressedBitAsync(repositoryRoot, "docs/.gitkeep", SuppressionKind.AssumeUnchanged);
        await File.WriteAllTextAsync(gitKeepPath, "diverged\n");

        var nestedPath = Path.Combine(repositoryRoot, "docs", "copied-vault");
        await CreateNestedGitRepositoryDirectoryAsync(nestedPath);

        // First restart: the suppressed-entry census runs before `add -A`/the gitlink check, so its
        // fault wins — the gitlink is not even named yet.
        var firstException = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/.gitkeep", firstException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("copied-vault", firstException.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
        var lsFilesAfterFirstRefusal = await _git.RunOrThrowAsync(repositoryRoot, ["ls-files", "--stage"]);
        Assert.DoesNotContain("160000", lsFilesAfterFirstRefusal.StandardOutput, StringComparison.Ordinal);

        // Fix only the suppressed divergence — restore the working tree to match HEAD. The bit itself
        // is never cleared (Decision 3), so the census still runs on the next restart, but now reports
        // Matches for this path.
        await File.WriteAllTextAsync(gitKeepPath, string.Empty);

        // Second restart: the suppressed entry is harmless now, so the deferred gitlink fault fires —
        // proving the first refusal did not silently lose it.
        var secondException = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureRepositoryAsync());
        Assert.Contains("docs/copied-vault", secondException.Message, StringComparison.Ordinal);

        Assert.Equal("1", (await _git.RunOrThrowAsync(repositoryRoot, ["rev-list", "--count", "HEAD"])).StandardOutput.Trim());
    }

    /// <summary>
    /// Builds a repository entirely outside <see cref="ContentRepositoryService"/> — its own
    /// initialization, its own author, its own layout — standing in for one adopted from elsewhere
    /// rather than one this service created on an earlier call.
    /// </summary>
    private async Task CreateForeignRepositoryAsync(string repositoryRoot, bool withDocsDirectory)
    {
        Directory.CreateDirectory(repositoryRoot);
        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);

        if (withDocsDirectory)
        {
            Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "existing-page.md"), "# Existing\n");
            await _git.RunOrThrowAsync(repositoryRoot, ["add", "docs/existing-page.md"]);
        }
        else
        {
            // Content lives outside docs/ entirely — a foreign repository has no reason to know
            // ZeroWiki's layout convention.
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "README.md"), "# Somebody else's repository\n");
            await _git.RunOrThrowAsync(repositoryRoot, ["add", "README.md"]);
        }

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "a commit ZeroWiki did not make"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Somebody Else",
                ["GIT_AUTHOR_EMAIL"] = "somebody@example.com",
                ["GIT_COMMITTER_NAME"] = "Somebody Else",
                ["GIT_COMMITTER_EMAIL"] = "somebody@example.com",
            });
    }

    private async Task CreateNestedGitRepositoryDirectoryAsync(string nestedPath)
    {
        Directory.CreateDirectory(nestedPath);
        await _git.RunOrThrowAsync(nestedPath, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "note.md"), "# Note\n");
        await _git.RunOrThrowAsync(nestedPath, ["add", "note.md"]);
        await _git.RunOrThrowAsync(
            nestedPath,
            ["commit", "-m", "inner commit"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Inner",
                ["GIT_AUTHOR_EMAIL"] = "inner@example.com",
                ["GIT_COMMITTER_NAME"] = "Inner",
                ["GIT_COMMITTER_EMAIL"] = "inner@example.com",
            });
    }

    /// <summary>Extracts the object SHA of the (single) <c>160000</c> gitlink entry in <c>ls-files --stage</c> output.</summary>
    private static string? ExtractGitlinkSha(string lsFilesStageOutput)
    {
        foreach (var line in lsFilesStageOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("160000 ", StringComparison.Ordinal))
            {
                return line.Split(' ')[1];
            }
        }

        return null;
    }

    private string RepositoryRoot => Path.Combine(_dataRoot, "wiki");

    /// <summary>
    /// <c>GIT_CONFIG_GLOBAL</c> pointed at a path this fixture never writes, isolating "is this path
    /// ignored" probes (<c>git check-ignore</c>) from whatever the developer's or CI runner's own
    /// global/user gitconfig happens to set (2.6) — this repo has already been bitten by exactly
    /// this class of leak once, with a global <c>credential.helper</c> flaking a clone test. The
    /// seeded rule itself no longer consults git at all (it is a plain text-line comparison), so
    /// this is only needed for the tests' own functional-proof assertions that a path really is
    /// ignored, not for the code under test.
    /// Edge: does not neutralise <c>/etc/gitconfig</c> (system-level) or a repository's own
    /// <c>$GIT_DIR/info/exclude</c> — only user/global config is overridden.
    /// </summary>
    private IReadOnlyDictionary<string, string> PinnedGitEnvironment =>
        new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = Path.Combine(_dataRoot, "unused-global-gitconfig") };

    private ContentRepositoryService CreateService(TimeSpan? writeLockTimeout = null) =>
        new(
            new ContentPaths(_dataRoot),
            _git,
            new GitHookInstaller(_git),
            NullLogger<ContentRepositoryService>.Instance,
            Options.Create(new ContentStorageOptions
            {
                DataRoot = _dataRoot,
                WriteLockTimeout = writeLockTimeout ?? TimeSpan.FromSeconds(10),
            }));

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

        // D17, §6 block D4 continuation round three: pinned alongside the two settings above so a
        // CRLF-bearing file can no longer make `add -A` warn regardless of what the host's own
        // ~/.gitconfig sets.
        Assert.Equal(
            "false",
            (await _git.RunOrThrowAsync(repositoryRoot, ["config", "core.autocrlf"])).StandardOutput.Trim());
    }

    private async Task AssertPorcelainIsEmptyAsync(string repositoryRoot)
    {
        var status = await _git.RunOrThrowAsync(repositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }
}
