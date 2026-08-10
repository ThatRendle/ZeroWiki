using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
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

        return await _clientGit.RunAsync(workingDirectory, arguments, env, cancellation.Token);
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
