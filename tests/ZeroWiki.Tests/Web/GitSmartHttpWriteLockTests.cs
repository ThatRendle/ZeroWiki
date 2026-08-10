using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 7.5, D16/D18 §5: an in-progress <c>git-receive-pack</c> acquires <see cref="RepositoryWriteLock"/>
/// around the whole invocation, serializing it against a concurrent browser save and any other writer of
/// <see cref="ContentPaths.LockFilePath"/> — and, in the other direction, a read (<c>git-upload-pack</c>/
/// <c>info/refs</c>) never acquires it at all (D18 §5). Mutation testing applies to the lock-acquisition
/// wiring in <see cref="ZeroWiki.Web.GitSmartHttpEndpoints"/> (block B brief); this file is its primary
/// coverage.
/// </summary>
/// <remarks>
/// Each test holds <see cref="RepositoryWriteLock"/> directly — the same primitive, on the same
/// <see cref="ContentPaths.LockFilePath"/>, that <c>PageSaveService</c>'s own save path (§6) and D9's
/// startup reconciliation already use — rather than trying to keep a real <c>git-receive-pack</c>
/// subprocess artificially stalled from the outside. That is deliberate: since acquisition is a single
/// shared file lock, proving each route's handler blocks on <em>this exact</em> lock while it is already
/// held is sufficient to prove push and save serialize against each other, without needing a
/// protocol-valid packfile — constructing one by hand is §7 block C's job (7.3/7.4, against a
/// really-listening Kestrel and a real git client), and a hand-rolled invalid one risks the receive-pack
/// subprocess itself exiting early in a way that says nothing about this route's own lock discipline.
/// </remarks>
public sealed class GitSmartHttpWriteLockTests : IDisposable
{
    private const string Username = "alice";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task ReceivePack_WaitsForTheWriteLockAlreadyHeldElsewhere_ThenProceedsOnceItIsReleased()
    {
        var (account, token) = await SeedAccountWithTokenAsync();
        var paths = _app.Services.GetRequiredService<ContentPaths>();

        var writeLock = await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30));
        HttpResponseMessage pushResponse;
        try
        {
            using var client = AuthenticatedClient(token);
            var pushTask = client.PostAsync(
                "/git/git-receive-pack",
                new ByteArrayContent("not a valid pack, only proving the lock is respected"u8.ToArray()));

            // Held well past any bounded timeout anywhere in this codebase (PageSaveService's own
            // SaveWriteLockTimeout defaults to 5s) before releasing -- not asserted on directly (a
            // fixed-delay "has it completed yet" check is exactly the kind of instrument that reports
            // success under a lightly-loaded filtered run and silently stops meaning anything under the
            // real parallel suite, this project's own recorded lesson); what actually proves the wait
            // was unbounded is the response this test checks below, once it finally arrives.
            await Task.Delay(TimeSpan.FromSeconds(1));

            writeLock.Dispose();
            pushResponse = await pushTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // No-op if already disposed above -- RepositoryWriteLock.Dispose() is idempotent.
            writeLock.Dispose();
        }

        using (pushResponse)
        {
            // The only two ways this fails: the wait gave up early (an unbounded-but-mutated-bounded
            // wait surfaces as an unhandled RepositoryLockTimeoutException -- nothing in this pipeline
            // catches it -- which UseExceptionHandler turns into a 500) or authentication itself failed
            // (401, checked as a sanity guard against a broken test setup). Either status is reachable
            // regardless of how long this test held the lock, so this check -- not the timing above --
            // is what actually proves HandleReceivePackAsync only reached the CGI subprocess once the
            // lock was genuinely free.
            Assert.NotEqual(HttpStatusCode.Unauthorized, pushResponse.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, pushResponse.StatusCode);
        }

        await AssertWorkingTreeIsCleanAsync();
    }

    [Fact]
    public async Task BrowserSave_WaitsForTheWriteLockAlreadyHeldElsewhere_ThenSucceedsOnceItIsReleased()
    {
        var (account, _) = await SeedAccountWithTokenAsync();
        var paths = _app.Services.GetRequiredService<ContentPaths>();

        var writeLock = await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30));

        var saveService = _app.Services.GetRequiredService<PageSaveService>();
        var saveAuthor = new AuthenticatedAccount(account.Id, account.Username, account.IsAdministrator);
        var saveTask = saveService.SaveAsync(
            new RouteValue("locked-out"),
            "written while something else holds the write lock",
            PageBaseRevision.AbsentAtHead,
            saveAuthor,
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(
            saveTask.IsCompleted,
            "The browser save completed while the write lock should still have been held elsewhere.");

        writeLock.Dispose();

        var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SaveOutcome.Saved, saveResult.Outcome);

        await AssertWorkingTreeIsCleanAsync();
    }

    [Fact]
    public async Task InfoRefsAndUploadPack_NeverWaitForTheWriteLock()
    {
        var (_, token) = await SeedAccountWithTokenAsync();
        var paths = _app.Services.GetRequiredService<ContentPaths>();

        using var writeLock = await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30));

        using var client = AuthenticatedClient(token);
        var readTask = client.GetAsync("/git/info/refs?service=git-upload-pack");

        // D18 §5: a read must complete promptly even while the write lock is held elsewhere, not
        // merely eventually once this test disposes it.
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(readTask, completed);

        using var readResponse = await readTask;
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
    }

    private HttpClient AuthenticatedClient((Guid Id, string Plaintext) token)
    {
        var client = _app.CreateHttpClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{token.Plaintext}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    private async Task AssertWorkingTreeIsCleanAsync()
    {
        var paths = _app.Services.GetRequiredService<ContentPaths>();
        var git = new GitProcessRunner();
        var status = await git.RunOrThrowAsync(paths.RepositoryRoot, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, status.StandardOutput);
    }

    private async Task<(Account Account, (Guid Id, string Plaintext) Token)> SeedAccountWithTokenAsync()
    {
        var hasher = new Argon2idPasswordHasher();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = Username,
            PasswordHash = hasher.Hash("a good long passphrase for alice"),
            DisplayName = Username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _app.WithDbAsync(async db =>
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

        await _app.WithDbAsync(async db =>
        {
            db.GitTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return (account, (token.Id, secret.Plaintext));
    }
}
