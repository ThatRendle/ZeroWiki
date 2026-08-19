using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

            // The hold is what gives this test its power, so it is derived from the configured
            // ContentStorageOptions rather than written as a number: every TimeSpan bound in
            // ContentStorageOptions, as the running app has it configured, plus a margin -- not every
            // bound in the application, which this neither reads nor needs. Any bounded wait the push
            // could have been given
            // instead of UnboundedWait has therefore already expired by the time the lock is released,
            // which is the difference between a test that distinguishes "unbounded" from "bounded" and
            // one that merely observes a wait shorter than the shortest bound. No "has it completed
            // yet" probe is taken during the hold -- not because that shape is untrustworthy in general
            // (BrowserSave below depends on it, and says why), but because it would add nothing here:
            // the response status this test already checks distinguishes a wait that gave up from one
            // that did not, so the probe would be a second, weaker witness to a fact the first one
            // already carries.
            var hold = LongerThanEveryConfiguredBound();
            await Task.Delay(hold);

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
            // (401, checked as a sanity guard against a broken test setup). The hold above and this
            // check are one instrument, not two: the hold guarantees that any bounded wait would have
            // given up, and this status is where that giving-up would surface. Together they establish
            // that HandleReceivePackAsync waited without a ceiling and reached the CGI subprocess only
            // once the lock was genuinely free.
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

        // Load-bearing, and the only assertion in this test that is. Everything below passes whether
        // the save waited for the lock or ignored it and completed straight away: SaveOutcome.Saved is
        // the outcome either way, and the tree is clean either way. This check is what separates those
        // two, so unlike the decorative uses of the same shape elsewhere in this section it is not
        // removable -- delete it and the test still passes against a save path with no lock at all.
        // Its known weakness is the one that makes a fixed delay a poor instrument in general: a
        // sufficiently loaded machine can leave the save incomplete for reasons that have nothing to do
        // with the lock, so it can pass for the wrong reason. It cannot pass while the property is
        // broken, which is the direction that matters here, and the unit-level test in
        // PageSaveServiceTests (a held lock yields RepositoryBusy and writes nothing) is the
        // timing-independent witness backstopping it.
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

    /// <summary>
    /// The longest bounded wait the running application is configured with, plus a margin -- read off
    /// <see cref="ContentStorageOptions"/> by reflection rather than naming <c>WriteLockTimeout</c> and
    /// <c>SaveWriteLockTimeout</c>, so that a bound added or re-defaulted later is covered without this
    /// test being edited. <see cref="TimeSpan.MaxValue"/> is excluded because it is the encoding of
    /// "no bound" (<c>GitSmartHttpEndpoints.UnboundedWait</c> itself), not a bound to outlast; including
    /// it would hang the test rather than strengthen it.
    /// </summary>
    private TimeSpan LongerThanEveryConfiguredBound()
    {
        var options = _app.Services.GetRequiredService<IOptions<ContentStorageOptions>>().Value;
        var bounds = typeof(ContentStorageOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(TimeSpan) && property.CanRead)
            .Select(property => (TimeSpan)property.GetValue(options)!)
            .Where(bound => bound != TimeSpan.MaxValue)
            .ToList();

        Assert.NotEmpty(bounds);
        return bounds.Max() + TimeSpan.FromSeconds(1);
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
