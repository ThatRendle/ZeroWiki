using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// §8 block B (D19): the whole trigger-to-broadcast path exercised through the real endpoint, over a
/// really-listening Kestrel and a real <c>git</c> client (task 7.3/7.4's own justification for why this
/// layer needs a real server, reused here). Subscribes to the real, production <see cref="IPageChangeNotifier"/>
/// singleton directly — the same seam a Blazor component uses — rather than substituting a fake, so this
/// proves the actual wiring: <c>GitSmartHttpEndpoints.HandleReceivePackAsync</c> captures HEAD inside the
/// write lock and <see cref="PushReactionService"/> reacts to it off-request.
/// </summary>
/// <remarks>
/// The rejected-push case is deliberately not "two real git push clients, one stale": that shape tests
/// the client's own preflight refusal (it never even sends the <c>POST</c> once a freshly-fetched
/// <c>info/refs</c> advertisement shows the push cannot fast-forward) and never reaches
/// <c>git-receive-pack</c> at all. Forcing a genuine server-side rejection replays a real push's captured
/// raw bytes a second time, once the server's own <c>HEAD</c> has moved past the request's embedded
/// old-sha — exactly what D16's write-lock race produces in production, reproduced here by construction
/// (see the DEVLOG, §8 block B thread, and <see cref="ZeroWikiAppFactory.CapturedReceivePackRequests"/>).
/// </remarks>
public sealed class PushReactionEndpointTests
{
    private const string Username = "alice";
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(10);

    private readonly GitProcessRunner _clientGit = new();

    [Fact]
    public async Task RealPush_NotifiesExactlyTheChangedRoute_AndNeverAViewerOnAnUntouchedRoute()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, token.Plaintext);
        var notifier = factory.Services.GetRequiredService<IPageChangeNotifier>();

        var touchedRoute = PageRouteCodec.Encode("page.md");
        var untouchedRoute = PageRouteCodec.Encode("untouched.md");
        var touchedNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var untouchedNotifiedCount = 0;

        using var touchedSubscription = notifier.Subscribe(touchedRoute, () =>
        {
            touchedNotified.TrySetResult();
            return Task.CompletedTask;
        });
        using var untouchedSubscription = notifier.Subscribe(untouchedRoute, () =>
        {
            Interlocked.Increment(ref untouchedNotifiedCount);
            return Task.CompletedTask;
        });

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");
        await RunClientGitOrThrowAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        await File.WriteAllTextAsync(Path.Combine(clonePath, "docs", "page.md"), "# Hello\n");
        await RunClientGitOrThrowAsync(clonePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(clonePath, ["commit", "-q", "-m", "add page"], ClientCommitIdentity());
        var push = await RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(push.Succeeded, $"push failed: {push.StandardError}");

        // The reaction runs off-request (D19 decision 1) -- the push's own HTTP response has already
        // completed by the time it fires, so this waits for the real, independent broadcast to land.
        await touchedNotified.Task.WaitAsync(NotificationTimeout);

        // The broadcast loop is a single sequential pass over the process's subscriptions (PageChangeNotifier),
        // so by the time the touched route's own callback has run, the untouched route's membership
        // check has already been decided either way; this margin only guards against ConcurrentDictionary's
        // unspecified enumeration order placing the untouched entry after the touched one in that same pass.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, untouchedNotifiedCount);
    }

    [Fact]
    public async Task ReplayingACapturedPushASecondTime_ForcesAServerSideRejection_AndReactsToNothing()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, token.Plaintext);
        var paths = factory.Services.GetRequiredService<ContentPaths>();
        var notifier = factory.Services.GetRequiredService<IPageChangeNotifier>();

        var route = PageRouteCodec.Encode("page.md");
        var notificationCount = 0;
        using var subscription = notifier.Subscribe(route, () =>
        {
            Interlocked.Increment(ref notificationCount);
            return Task.CompletedTask;
        });

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");
        await RunClientGitOrThrowAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        await File.WriteAllTextAsync(Path.Combine(clonePath, "docs", "page.md"), "# Hello\n");
        await RunClientGitOrThrowAsync(clonePath, ["add", "-A"]);
        await RunClientGitOrThrowAsync(clonePath, ["commit", "-q", "-m", "add page"], ClientCommitIdentity());
        var push = await RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(push.Succeeded, $"push failed: {push.StandardError}");

        // Waits for the real push's own genuine reaction to land before touching the replay, so the
        // count below starts from a known baseline (exactly one notification, not "however many have
        // happened so far").
        await WaitUntilAsync(() => notificationCount >= 1, NotificationTimeout);
        Assert.Equal(1, notificationCount);

        var headAfterRealPush = await RunOrThrowAsync(paths.RepositoryRoot, ["rev-parse", "HEAD"]);

        var captured = Assert.Single(factory.CapturedReceivePackRequests);

        using var client = AuthenticatedClient(factory, token);
        using var content = new ByteArrayContent(captured.Body);
        content.Headers.ContentType = captured.ContentType is null
            ? null
            : MediaTypeHeaderValue.Parse(captured.ContentType);
        if (captured.ContentEncoding is { Length: > 0 } contentEncoding)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        using var replayResponse = await client.PostAsync("/git/git-receive-pack", content);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);

        var headAfterReplay = await RunOrThrowAsync(paths.RepositoryRoot, ["rev-parse", "HEAD"]);
        Assert.Equal(headAfterRealPush.StandardOutput, headAfterReplay.StandardOutput);

        // A short, bounded margin for a reaction that, if the endpoint were wrong, would have already
        // fired synchronously inside the request it just awaited above -- not a "wait and hope"
        // instrument for a positive event, since none is expected here.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task NoOpPush_ReactsToNothing()
    {
        using var factory = ZeroWikiAppFactory.WithRealServer();
        var (_, token) = await SeedAccountWithTokenAsync(factory);
        var remoteUrl = BuildRemoteUrl(factory.RealServerAddress, token.Plaintext);
        var notifier = factory.Services.GetRequiredService<IPageChangeNotifier>();

        var route = PageRouteCodec.Encode("page.md");
        var notificationCount = 0;
        using var subscription = notifier.Subscribe(route, () =>
        {
            Interlocked.Increment(ref notificationCount);
            return Task.CompletedTask;
        });

        using var scratch = new TempDirectory();
        var clonePath = Path.Combine(scratch.Path, "clone");
        await RunClientGitOrThrowAsync(scratch.Path, ["clone", "--quiet", remoteUrl, clonePath]);

        // Nothing new to send -- git either short-circuits client-side or sends a genuine empty
        // ref-update command set; either way D19 §2 says both are already safe.
        var push = await RunClientGitAsync(clonePath, ["push", "--quiet", "origin", "HEAD"]);
        Assert.True(push.Succeeded, $"a no-op push should still report success: {push.StandardError}");

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, notificationCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    // `-c credential.helper=` (empty) is prepended to every argument list this file hands to `_clientGit`
    // below, on all three call sites — this file builds a real remote URL with an embedded token
    // (BuildRemoteUrl) and drives a real git client against it exactly as GitSmartHttpRealClientTests.cs
    // does, so it carries the same hazard that file's own pin exists to close: Homebrew's *system*-scope
    // gitconfig (not `--global`) sets `credential.helper = osxkeychain`, and under the full parallel
    // suite a credential cached by one authenticated test has been observed offered to a different test's
    // git process. Disabling the helper for these invocations stops that, and stops this suite writing
    // real credentials into the developer's OS keychain at all.
    private async Task<GitProcessResult> RunOrThrowAsync(string workingDirectory, IReadOnlyList<string> arguments) =>
        await _clientGit.RunOrThrowAsync(workingDirectory, PinCredentialHelper(arguments));

    private static string BuildRemoteUrl(Uri baseAddress, string credential) =>
        $"{baseAddress.Scheme}://{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(credential)}@{baseAddress.Authority}/git";

    private static List<string> PinCredentialHelper(IReadOnlyList<string> arguments)
    {
        var pinned = new List<string>(arguments.Count + 2) { "-c", "credential.helper=" };
        pinned.AddRange(arguments);
        return pinned;
    }

    /// <summary>
    /// <see cref="ZeroWikiAppFactory.CreateHttpClient"/> pins its base address to the fixed
    /// <c>https://localhost</c> the in-memory <c>TestServer</c> variant uses; <see cref="ZeroWikiAppFactory.WithRealServer"/>
    /// listens on a real, dynamically-chosen port instead (<see cref="ZeroWikiAppFactory.RealServerAddress"/>),
    /// so this replay client must be built against that address explicitly.
    /// </summary>
    private HttpClient AuthenticatedClient(ZeroWikiAppFactory factory, IssuedToken token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = factory.RealServerAddress,
        });
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{token.Plaintext}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    private async Task<GitProcessResult> RunClientGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        using var cancellation = new CancellationTokenSource(ClientTimeout);
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        return await _clientGit.RunAsync(workingDirectory, PinCredentialHelper(arguments), env, cancellation.Token);
    }

    private async Task RunClientGitOrThrowAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var result = await RunClientGitAsync(workingDirectory, arguments);
        if (!result.Succeeded)
        {
            throw new GitProcessException(arguments, result.ExitCode, result.StandardError);
        }
    }

    private async Task RunClientGitOrThrowAsync(
        string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environmentVariables)
    {
        using var cancellation = new CancellationTokenSource(ClientTimeout);
        var env = new Dictionary<string, string>(environmentVariables) { ["GIT_TERMINAL_PROMPT"] = "0" };
        var result = await _clientGit.RunAsync(workingDirectory, PinCredentialHelper(arguments), env, cancellation.Token);
        if (!result.Succeeded)
        {
            throw new GitProcessException(arguments, result.ExitCode, result.StandardError);
        }
    }

    private static IReadOnlyDictionary<string, string> ClientCommitIdentity() =>
        new GitAuthor("Obsidian Vault (simulated)", "obsidian-client@zerowiki.invalid").ToEnvironmentVariables();

    private static async Task<(Account Account, IssuedToken Token)> SeedAccountWithTokenAsync(ZeroWikiAppFactory factory)
    {
        var hasher = new Argon2idPasswordHasher();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = Username,
            PasswordHash = hasher.Hash("a good long passphrase, unrelated to the git token"),
            DisplayName = Username,
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

    private readonly record struct IssuedToken(Guid Id, string Plaintext);

    /// <summary>A throwaway directory this test suite drives real git clients inside of.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"zerowiki-push-reaction-client-{Guid.NewGuid():n}");

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
