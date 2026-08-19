using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Tasks 7.3/7.4: the property block A and block B could not establish — that a real, unmodified
/// <c>git</c> client completes a real clone, fetch and push against the application <em>as it
/// actually runs</em>. <c>TestServer</c> has no listening socket, and <c>git</c> is a separate OS
/// process that must dial a real port, so every test here boots a real Kestrel listener
/// (<see cref="ZeroWikiAppFactory.WithRealServer"/>) and drives the real <c>git</c> binary against
/// it via <see cref="GitProcessRunner"/> — the same subprocess abstraction the application itself
/// uses, reused here for the client side too rather than hand-rolled.
/// </summary>
/// <remarks>
/// Every client-side invocation carries <c>GIT_TERMINAL_PROMPT=0</c> and a bounded
/// <see cref="ClientTimeout"/>: an unauthenticated request that this suite expects to fail must
/// fail promptly at the client, never hang waiting on a TTY prompt nobody can answer.
/// </remarks>
public sealed class GitSmartHttpRealClientTests
{
    private const string Username = "alice";
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    private readonly GitProcessRunner _clientGit = new();

    [Fact]
    public async Task AuthenticatedClient_ClonesEditsAndPushes_AndTheServersWorkingTreeReflectsIt()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory, Username);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");

        var clone = await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);
        Assert.True(clone.Succeeded, $"clone failed: {clone.StandardError}");
        Assert.True(File.Exists(Path.Combine(clonePath, "docs", ".gitkeep")), "a fresh repository's initial commit should have cloned docs/.gitkeep.");

        // Fetch: an explicit, otherwise-pointless fetch against an unchanged remote still has to
        // succeed over the real transport -- this is the negative space of 7.4's fast-forward
        // assertions below, which only prove fetch indirectly via a second clone.
        var fetch = await RunClientGitAsync(clonePath, ["fetch", "--quiet", "origin"]);
        Assert.True(fetch.Succeeded, $"fetch failed: {fetch.StandardError}");

        var pagePath = Path.Combine(clonePath, "docs", "page.md");
        await File.WriteAllTextAsync(pagePath, "# Hello\n\nwritten by a real git client\n");
        await RunClientGitOrThrowAsync(clonePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(
            clonePath,
            ["commit", "-q", "-m", "add page"],
            ClientCommitIdentity());

        var push = await RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(push.Succeeded, $"push failed: {push.StandardError}");

        // 7.4: the working tree, not just the ref. Read the file the app actually renders directly
        // off disk, bypassing git entirely.
        var paths = factory.Services.GetRequiredService<ContentPaths>();
        var serverSidePagePath = Path.Combine(paths.WorkingTree, "page.md");
        Assert.True(File.Exists(serverSidePagePath), "the pushed file should exist in the server's working tree.");
        Assert.Equal(
            "# Hello\n\nwritten by a real git client\n",
            await File.ReadAllTextAsync(serverSidePagePath));

        await AssertServerWorkingTreeIsCleanAsync(paths);
    }

    /// <summary>
    /// 9.2's server→client direction, untested before this block: everything above this test proves
    /// a real client's write reaches the server; nothing proved the reverse — that a save made through
    /// the app's own write path (<see cref="PageSaveService"/>, not a hand-rolled <c>git commit</c>) is
    /// what a real client actually pulls. If commit-on-save lands the commit but leaves the branch ref
    /// behind (detached HEAD, wrong branch advanced, or a commit written but never referenced), a clone
    /// still succeeds — it would just yield the file's old bytes, or no file at all. Asserting on the
    /// pulled file's bytes, not merely that the clone succeeded, is what would actually catch that.
    /// </summary>
    [Fact]
    public async Task PageSavedThroughTheApp_IsWhatARealClientClones()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (account, token) = await SeedAccountWithTokenAsync(factory, Username);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);

        const string SavedContent = "# Saved through the app\n\nwritten via PageSaveService, not git directly\n";
        var saveService = factory.Services.GetRequiredService<PageSaveService>();
        var saveAuthor = new AuthenticatedAccount(account.Id, account.Username, account.IsAdministrator);
        var saveResult = await saveService.SaveAsync(
            new RouteValue("app-saved-page"),
            SavedContent,
            PageBaseRevision.AbsentAtHead,
            saveAuthor,
            CancellationToken.None);
        Assert.Equal(SaveOutcome.Saved, saveResult.Outcome);

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");

        var clone = await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);
        Assert.True(clone.Succeeded, $"clone failed: {clone.StandardError}");

        var pulledPagePath = Path.Combine(clonePath, "docs", "app-saved-page.md");
        Assert.True(
            File.Exists(pulledPagePath),
            "a page saved through the app's write path should have been present in the cloned working tree.");
        Assert.Equal(SavedContent, await File.ReadAllTextAsync(pulledPagePath));
    }

    /// <summary>
    /// Task 10.2: the interleaving itself. Every other lock test in this suite races a lock held by
    /// the <em>test process</em> — <see cref="GitSmartHttpWriteLockTests"/> holds
    /// <see cref="RepositoryWriteLock"/> directly and proves each route waits on it. That establishes
    /// each side's discipline separately; it never puts the two real writers in flight at once, so it
    /// cannot see the outcome `10.2` actually names: after a genuine interleaving, both writes are
    /// present, neither is lost, and the working tree is clean. This test lives in this file rather
    /// than beside those because D3's racer is a <b>second OS process</b>, and only this file's
    /// harness (a real Kestrel listener and the real <c>git</c> binary) produces one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interleaving is made deterministic by a <c>pre-receive</c> hook that parks the push at a
    /// known point and waits for this test to release it. The hook takes no lock itself (D3 forbids
    /// that, on pain of deadlock) — it does not have to: the app holds the write lock around the
    /// <em>whole</em> <c>git http-backend</c> invocation (§7.5), and the hook runs as a child of that
    /// invocation, so a parked hook means a held lock. Overwriting
    /// <see cref="GitHookInstaller.PreReceiveHookName"/> after startup is safe for the same reason
    /// startup can overwrite it: the shipped hook is a permanent no-op (D19).
    /// </para>
    /// <para>
    /// The 300ms pause before the push is released is <b>pacing, not evidence</b>, and asserts
    /// nothing. It once carried a non-completion check; that check was removed because it could not
    /// do the job its own comment claimed — under the mutant that actually removed the push side's
    /// lock, it <em>passed</em>, so it never named the failure early, and under correct code it can
    /// never fail at all. What proves serialization is that the save's result and the final tree are
    /// read <em>after</em> the push has been released and has completed: with either side's lock
    /// removed the save commits while <c>receive-pack</c> is still mid-transaction, and the push that
    /// follows can no longer fast-forward from the sha it advertised. The pause survives only so the
    /// save has reached the lock before the push is let go, which is what makes the interleaving the
    /// one this test means to exercise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PushAndBrowserSaveInterleaved_AreSerialized_AndBothWritesSurviveOnACleanTree()
    {
        const string PushedContent = "# From a push\n\nwritten by a real git client, mid-interleaving\n";
        const string SavedContent = "# From the browser\n\nwritten by PageSaveService, mid-interleaving\n";
        const string PushCommitSubject = "add a page by push, parked in pre-receive";

        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (account, token) = await SeedAccountWithTokenAsync(factory, Username);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);
        var paths = factory.Services.GetRequiredService<ContentPaths>();

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");
        await RunClientGitOrThrowAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        await File.WriteAllTextAsync(Path.Combine(clonePath, "docs", "from-push.md"), PushedContent);
        await RunClientGitOrThrowAsync(clonePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(
            clonePath,
            ["commit", "-q", "-m", PushCommitSubject],
            ClientCommitIdentity());

        var reachedMarker = Path.Combine(scratch.Path, "pre-receive-reached");
        var releaseMarker = Path.Combine(scratch.Path, "pre-receive-release");
        await InstallStallingPreReceiveHookAsync(paths.RepositoryRoot, reachedMarker, releaseMarker);

        var pushTask = RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
        await WaitForPreReceiveToParkAsync(reachedMarker, pushTask);

        var saveService = factory.Services.GetRequiredService<PageSaveService>();
        var saveAuthor = new AuthenticatedAccount(account.Id, account.Username, account.IsAdministrator);
        var saveTask = saveService.SaveAsync(
            new RouteValue("from-browser"),
            SavedContent,
            PageBaseRevision.AbsentAtHead,
            saveAuthor,
            CancellationToken.None);

        // Pacing, not proof: give the save time to reach the write lock before the push is released,
        // so that the interleaving this test is about actually occurs. Nothing is asserted here --
        // see this test's remarks for why a non-completion check would be measuring the machine's load.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await File.WriteAllTextAsync(releaseMarker, string.Empty);

        var push = await pushTask;
        Assert.True(push.Succeeded, $"push failed: {push.StandardError}");

        var saveResult = await saveTask.WaitAsync(ClientTimeout);
        Assert.Equal(SaveOutcome.Saved, saveResult.Outcome);

        // Neither write lost: each file's own bytes on disk, and both paths committed at HEAD rather
        // than merely sitting in the working tree.
        Assert.Equal(PushedContent, await File.ReadAllTextAsync(Path.Combine(paths.WorkingTree, "from-push.md")));
        Assert.Equal(SavedContent, await File.ReadAllTextAsync(Path.Combine(paths.WorkingTree, "from-browser.md")));

        var tracked = await RunClientGitAsync(paths.RepositoryRoot, ["ls-tree", "-r", "--name-only", "HEAD"]);
        Assert.True(tracked.Succeeded, $"ls-tree failed: {tracked.StandardError}");
        Assert.Contains("docs/from-push.md", tracked.StandardOutput);
        Assert.Contains("docs/from-browser.md", tracked.StandardOutput);

        var subjects = await RunClientGitAsync(paths.RepositoryRoot, ["log", "--format=%s"]);
        Assert.True(subjects.Succeeded, $"log failed: {subjects.StandardError}");
        Assert.Contains(PushCommitSubject, subjects.StandardOutput);

        await AssertServerWorkingTreeIsCleanAsync(paths);
    }

    [Fact]
    public async Task NonFastForwardPush_IsRejected_AndTheServersWorkingTreeIsUnchanged()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory, Username);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);
        var paths = factory.Services.GetRequiredService<ContentPaths>();

        using var scratch = new TempDirectory();
        var cloneAheadPath = Path.Combine(scratch.Path, "clone-ahead");
        var cloneStalePath = Path.Combine(scratch.Path, "clone-stale");

        // Both clones start from the same initial state.
        Assert.True((await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, cloneAheadPath])).Succeeded);
        Assert.True((await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, cloneStalePath])).Succeeded);

        // The "ahead" clone commits and pushes first -- a genuine fast-forward, expected to succeed.
        await File.WriteAllTextAsync(Path.Combine(cloneAheadPath, "docs", "winner.md"), "the fast-forward push\n");
        await RunClientGitOrThrowAsync(cloneAheadPath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(cloneAheadPath, ["commit", "-q", "-m", "winner"], ClientCommitIdentity());
        var winningPush = await RunClientGitAsync(cloneAheadPath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(winningPush.Succeeded, $"the fast-forward push should have succeeded: {winningPush.StandardError}");

        // The "stale" clone never fetched the winner's commit -- its own commit, on the old parent,
        // cannot fast-forward the branch the remote is now at.
        await File.WriteAllTextAsync(Path.Combine(cloneStalePath, "docs", "loser.md"), "the rejected push\n");
        await RunClientGitOrThrowAsync(cloneStalePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(cloneStalePath, ["commit", "-q", "-m", "loser"], ClientCommitIdentity());
        var rejectedPush = await RunClientGitAsync(cloneStalePath, ["push", "--quiet", "origin", "HEAD"]);

        Assert.False(rejectedPush.Succeeded, "a non-fast-forward push must be rejected.");
        Assert.Contains("rejected", rejectedPush.StandardError, StringComparison.OrdinalIgnoreCase);

        // The server's working tree carries only the winning push's file -- the rejected attempt
        // wrote nothing, on disk or into history.
        Assert.True(File.Exists(Path.Combine(paths.WorkingTree, "winner.md")));
        Assert.False(File.Exists(Path.Combine(paths.WorkingTree, "loser.md")));

        await AssertServerWorkingTreeIsCleanAsync(paths);
    }

    /// <summary>
    /// 9.3's other half: the spec requires a rejected push to be recoverable by the client resolving
    /// the conflict locally (pull and merge), not a dead end — and the resolution must happen entirely
    /// at the client. This test picks up exactly where
    /// <see cref="NonFastForwardPush_IsRejected_AndTheServersWorkingTreeIsUnchanged"/> stops: after the
    /// rejection, the same client pulls (fetch + merge, a real three-way merge since the two clones
    /// diverged from a common ancestor) and pushes again. Asserting on the server's final working-tree
    /// <em>contents</em> — both files present with their own bytes — rather than just the second push's
    /// exit code is deliberate: a stale ref, a lock not released, or a dirty tree left behind by the
    /// first rejection could each let the second push report success while the server is still missing
    /// one of the two edits, and only reading the tree back catches that. The server side stays on
    /// plain <c>receive.denyCurrentBranch = updateInstead</c> throughout — no server-side setting is
    /// relaxed and no auto-merge, force, or reset happens outside the client's own repository.
    /// </summary>
    [Fact]
    public async Task NonFastForwardPush_AfterClientPullsAndMerges_SecondPushSucceedsWithBothEdits()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory, Username);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);
        var paths = factory.Services.GetRequiredService<ContentPaths>();

        using var scratch = new TempDirectory();
        var cloneAheadPath = Path.Combine(scratch.Path, "clone-ahead");
        var cloneStalePath = Path.Combine(scratch.Path, "clone-stale");

        // Both clones start from the same initial state.
        Assert.True((await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, cloneAheadPath])).Succeeded);
        Assert.True((await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, cloneStalePath])).Succeeded);

        // The "ahead" clone commits and pushes first -- a genuine fast-forward, expected to succeed.
        await File.WriteAllTextAsync(Path.Combine(cloneAheadPath, "docs", "winner.md"), "the fast-forward push\n");
        await RunClientGitOrThrowAsync(cloneAheadPath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(cloneAheadPath, ["commit", "-q", "-m", "winner"], ClientCommitIdentity());
        var winningPush = await RunClientGitAsync(cloneAheadPath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(winningPush.Succeeded, $"the fast-forward push should have succeeded: {winningPush.StandardError}");

        // The "stale" clone never fetched the winner's commit -- its own commit, on the old parent,
        // cannot fast-forward the branch the remote is now at.
        await File.WriteAllTextAsync(Path.Combine(cloneStalePath, "docs", "loser.md"), "the rejected push\n");
        await RunClientGitOrThrowAsync(cloneStalePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(cloneStalePath, ["commit", "-q", "-m", "loser"], ClientCommitIdentity());
        var rejectedPush = await RunClientGitAsync(cloneStalePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.False(rejectedPush.Succeeded, "the stale clone's first push must still be rejected.");
        // Pins the reason, not merely that it failed -- matches NonFastForwardPush_IsRejected_AndTheServersWorkingTreeIsUnchanged's
        // own assertion, so a push failing for an unrelated reason (bad URL, auth, transport) cannot pass this test.
        Assert.Contains("rejected", rejectedPush.StandardError, StringComparison.OrdinalIgnoreCase);

        // Resolution at the client (spec): pull -- fetch plus a real three-way merge, since the two
        // clones' commits diverged from a common ancestor rather than one simply being behind the other.
        var pull = await RunClientGitAsync(
            cloneStalePath, ["pull", "--quiet", "--no-rebase", "origin", "HEAD"], ClientCommitIdentity());
        Assert.True(pull.Succeeded, $"pull/merge at the client failed: {pull.StandardError}");

        var secondPush = await RunClientGitAsync(cloneStalePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(secondPush.Succeeded, $"the retried push after merging locally should succeed: {secondPush.StandardError}");

        // The server's working tree, read directly off disk: both edits, not merely a successful exit
        // code from the second push.
        Assert.True(File.Exists(Path.Combine(paths.WorkingTree, "winner.md")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingTree, "loser.md")));
        Assert.Equal("the fast-forward push\n", await File.ReadAllTextAsync(Path.Combine(paths.WorkingTree, "winner.md")));
        Assert.Equal("the rejected push\n", await File.ReadAllTextAsync(Path.Combine(paths.WorkingTree, "loser.md")));

        await AssertServerWorkingTreeIsCleanAsync(paths);
    }

    [Fact]
    public async Task UnauthenticatedClone_FailsAtTheClient()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        // No account needs to exist at all -- the request must be refused before any credential
        // lookup could even matter.
        _ = factory.RealServerAddress;
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, username: null, credential: null);

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");

        var clone = await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        Assert.False(clone.Succeeded, "an unauthenticated clone must fail at the client, not merely return a non-200.");
        // Confirms *why* it failed, not merely that it did: git never got as far as sending a
        // credential at all -- refused before it could even try, the same "no subprocess starts"
        // guarantee GitSmartHttpAuthenticationTests proves at the HTTP level.
        Assert.Contains("terminal prompts disabled", clone.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(Path.Combine(clonePath, ".git", "HEAD")),
            "a failed clone must not leave behind a repository.");
    }

    [Fact]
    public async Task RevokedTokenClone_FailsAtTheClient()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (account, token) = await SeedAccountWithTokenAsync(factory, Username);
        await RevokeTokenAsync(factory, account.Id, token.Id);

        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");

        var clone = await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        Assert.False(clone.Succeeded, "a clone using a revoked token must fail at the client.");
        // Confirms the failure is the server's 401 challenge (a real credential was presented and
        // rejected), distinguishing this from UnauthenticatedClone_FailsAtTheClient's "never even
        // tried" shape above.
        Assert.Contains("Authentication failed", clone.StandardError, StringComparison.Ordinal);
        Assert.False(
            File.Exists(Path.Combine(clonePath, ".git", "HEAD")),
            "a failed clone must not leave behind a repository.");
    }

    /// <summary>
    /// Obligation 10 (D18 §7, the task this section spent six sections not testing): an adopted
    /// repository may be checked out on a branch other than <c>main</c>, and nothing in the Smart
    /// HTTP surface may assume otherwise. This test's fixture is deliberately created on
    /// <c>master</c> -- never <c>ContentRepositoryService.DefaultBranch</c> -- <em>before</em> the
    /// application ever starts, so startup takes the adopted-repository path, not first-ever
    /// <c>git init -b main</c>. The branch identity asserted below is read from git itself on both
    /// ends (the fixture and the clone), never hardcoded as a literal the way a test that only ever
    /// exercises <c>main</c> could pass by accident.
    /// </summary>
    [Fact]
    public async Task AdoptedRepositoryOnANonMainBranch_ClonesAndPushesOnThatBranch()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-real-master-{Guid.NewGuid():n}");
        var fixturePaths = new ContentPaths(dataRoot);
        await CreateAdoptedFixtureRepositoryAsync(fixturePaths, branch: "master");

        var fixtureBranch = await ReadCheckedOutBranchAsync(fixturePaths.RepositoryRoot);
        Assert.Equal("refs/heads/master", fixtureBranch);

        try
        {
            using var factory = ZeroWikiAppFactory.WithRealServer(dataRoot);
            var (_, token) = await SeedAccountWithTokenAsync(factory, Username);
            var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, Username, token.Plaintext);

            using var scratch = new TempDirectory();
            var clonePath = Path.Combine(scratch.Path, "clone");

            var clone = await RunClientGitAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);
            Assert.True(clone.Succeeded, $"clone of the adopted non-main-branch repository failed: {clone.StandardError}");

            var clonedBranch = await ReadCheckedOutBranchAsync(clonePath);
            Assert.Equal(fixtureBranch, clonedBranch);
            Assert.Equal(
                "adopted content, pre-existing before ZeroWiki ever started\n",
                await File.ReadAllTextAsync(Path.Combine(clonePath, "docs", "index.md")));

            await File.WriteAllTextAsync(Path.Combine(clonePath, "docs", "index.md"), "edited on master\n");
            await RunClientGitOrThrowAsync(clonePath, ["add", "-A"]);
            await RunClientGitOrThrowAsync(clonePath, ["commit", "-q", "-m", "edit on master"], ClientCommitIdentity());
            var push = await RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
            Assert.True(push.Succeeded, $"push to the adopted master branch failed: {push.StandardError}");

            var paths = factory.Services.GetRequiredService<ContentPaths>();
            Assert.Equal("edited on master\n", await File.ReadAllTextAsync(Path.Combine(paths.WorkingTree, "index.md")));
            await AssertServerWorkingTreeIsCleanAsync(paths);

            var branchAfterPush = await ReadCheckedOutBranchAsync(paths.RepositoryRoot);
            Assert.Equal(fixtureBranch, branchAfterPush);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static string BuildRemoteUrl(Uri baseAddress, string? username, string? credential)
    {
        var userInfo = username is null
            ? string.Empty
            : credential is null
                ? $"{Uri.EscapeDataString(username)}@"
                : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(credential)}@";

        return $"{baseAddress.Scheme}://{userInfo}{baseAddress.Authority}/git";
    }

    private async Task<GitProcessResult> RunClientGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        using var cancellation = new CancellationTokenSource(ClientTimeout);
        var env = new Dictionary<string, string>(environmentVariables ?? new Dictionary<string, string>())
        {
            // A real git client asked to authenticate against a server it can't must not block this
            // suite waiting on a terminal prompt nobody can answer.
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        // Disable credential caching for every real client-side git invocation this file makes. Without
        // this, a real credential -- the URL-embedded token this file's own authenticated tests use --
        // gets offered to `git credential-<helper>`, which on this developer's machine resolves (via
        // Homebrew's *system*-scope gitconfig, not `--global`, so a `--global`-only probe reads this as
        // "no helper set") to `osxkeychain`: under the full parallel suite one authenticated test's
        // cached credential has been observed offered to a different test's git process, and the suite
        // has no business writing real credentials into the developer's OS keychain at all. `-c
        // credential.helper=` (empty) overrides every configured helper for this one invocation without
        // touching the developer's git config. Every real client-side git process this suite starts goes
        // through this one method -- see the class remarks for why that makes this the complete set.
        var pinnedArguments = new List<string>(arguments.Count + 2) { "-c", "credential.helper=" };
        pinnedArguments.AddRange(arguments);

        return await _clientGit.RunAsync(workingDirectory, pinnedArguments, env, cancellation.Token);
    }

    private async Task RunClientGitOrThrowAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var result = await RunClientGitAsync(workingDirectory, arguments, environmentVariables);
        if (!result.Succeeded)
        {
            throw new GitProcessException(arguments, result.ExitCode, result.StandardError);
        }
    }

    private static IReadOnlyDictionary<string, string> ClientCommitIdentity() => new GitAuthor(
        "Obsidian Vault (simulated)", "obsidian-client@zerowiki.invalid").ToEnvironmentVariables();

    private async Task<string> ReadCheckedOutBranchAsync(string repositoryRoot)
    {
        var result = await RunClientGitAsync(repositoryRoot, ["symbolic-ref", "HEAD"]);
        Assert.True(result.Succeeded, $"symbolic-ref HEAD failed in '{repositoryRoot}': {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private async Task CreateAdoptedFixtureRepositoryAsync(ContentPaths paths, string branch)
    {
        Directory.CreateDirectory(paths.WorkingTree);
        await File.WriteAllTextAsync(
            Path.Combine(paths.WorkingTree, "index.md"),
            "adopted content, pre-existing before ZeroWiki ever started\n");

        await RunClientGitOrThrowAsync(paths.RepositoryRoot, ["init", "-q", "-b", branch]);
        await RunClientGitOrThrowAsync(paths.RepositoryRoot, ["add", "-A"]);
        await RunClientGitOrThrowAsync(
            paths.RepositoryRoot,
            ["commit", "-q", "-m", "adopted content"],
            GitAuthor.System.ToEnvironmentVariables());
    }

    /// <summary>
    /// Overwrites the repository's <c>pre-receive</c> hook with one that parks every push until
    /// <paramref name="releaseMarkerPath"/> appears, announcing its arrival by creating
    /// <paramref name="reachedMarkerPath"/> first. The hooks directory is resolved with
    /// <c>git rev-parse --git-path hooks</c>, the same way <see cref="GitHookInstaller"/> resolves it,
    /// rather than assumed to be <c>.git/hooks</c>.
    /// </summary>
    /// <remarks>
    /// The hook drains stdin (git feeds <c>pre-receive</c> the ref updates there) and gives up after
    /// 30 seconds regardless, so a test that fails before releasing it cannot leave a push — or the
    /// write lock the app is holding around it — parked indefinitely.
    /// </remarks>
    private async Task InstallStallingPreReceiveHookAsync(
        string repositoryRoot,
        string reachedMarkerPath,
        string releaseMarkerPath)
    {
        var hooksPath = await RunClientGitAsync(repositoryRoot, ["rev-parse", "--git-path", "hooks"]);
        Assert.True(hooksPath.Succeeded, $"rev-parse --git-path hooks failed: {hooksPath.StandardError}");

        var hooksDirectory = hooksPath.StandardOutput.Trim();
        if (!Path.IsPathRooted(hooksDirectory))
        {
            hooksDirectory = Path.Combine(repositoryRoot, hooksDirectory);
        }

        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, GitHookInstaller.PreReceiveHookName);
        await File.WriteAllTextAsync(
            hookPath,
            $"""
            #!/bin/sh
            cat > /dev/null
            touch '{reachedMarkerPath}'
            waited=0
            while [ ! -e '{releaseMarkerPath}' ] && [ "$waited" -lt 300 ]; do
                sleep 0.1
                waited=$((waited + 1))
            done
            exit 0

            """);

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
    /// Waits until the stalling <c>pre-receive</c> hook reports it is running — i.e. the push is
    /// genuinely inside the lock-held <c>git http-backend</c> invocation — failing fast, and with the
    /// client's own stderr, if the push instead finished or died before ever reaching the hook.
    /// </summary>
    private static async Task WaitForPreReceiveToParkAsync(
        string reachedMarkerPath,
        Task<GitProcessResult> pushTask)
    {
        var deadline = DateTime.UtcNow + ClientTimeout;
        while (!File.Exists(reachedMarkerPath))
        {
            if (pushTask.IsCompleted)
            {
                var finished = await pushTask;
                Assert.Fail(
                    "The push completed without ever reaching the stalling pre-receive hook " +
                    $"(exit {finished.ExitCode}): {finished.StandardError}");
            }

            Assert.True(DateTime.UtcNow < deadline, "The push never reached the stalling pre-receive hook.");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private async Task AssertServerWorkingTreeIsCleanAsync(ContentPaths paths)
    {
        var status = await RunClientGitAsync(paths.RepositoryRoot, ["status", "--porcelain"]);
        Assert.True(status.Succeeded, $"git status failed: {status.StandardError}");
        Assert.Equal(string.Empty, status.StandardOutput);
    }

    private static async Task<(Account Account, IssuedToken Token)> SeedAccountWithTokenAsync(
        ZeroWikiAppFactory factory, string username)
    {
        var hasher = new Argon2idPasswordHasher();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.Hash("a good long passphrase, unrelated to the git token"),
            DisplayName = username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await factory.WithDbAsync(async db =>
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        });

        var generator = new SecretTokenGenerator();
        var secret = generator.Generate();
        var token = new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = secret.Hash,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await factory.WithDbAsync(async db =>
        {
            db.GitTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return (account, new IssuedToken(token.Id, secret.Plaintext));
    }

    private static async Task RevokeTokenAsync(ZeroWikiAppFactory factory, Guid accountId, Guid tokenId) =>
        await factory.WithDbAsync(async db =>
        {
            var token = await db.GitTokens.SingleAsync(t => t.AccountId == accountId && t.Id == tokenId);
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

    private readonly record struct IssuedToken(Guid Id, string Plaintext);

    /// <summary>A throwaway directory this test suite drives real git clients inside of.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"zerowiki-git-client-{Guid.NewGuid():n}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
