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

    private ContentRepositoryService CreateService() =>
        new(new ContentPaths(_dataRoot), _git, new GitHookInstaller(_git), NullLogger<ContentRepositoryService>.Instance);

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
