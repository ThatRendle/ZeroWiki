using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Proves "single-use" holds under genuine concurrency, and that the write lock is held over the
/// right span of the redemption — neither too little of it nor too much.
/// </summary>
/// <remarks>
/// <para>
/// This needs a file-backed database. The shared in-memory pattern used elsewhere is a single
/// connection, which cannot exhibit the two-writers race at all, so the happy path run twice would
/// pass against an implementation with no lock in it.
/// </para>
/// <para>
/// It also needs the rendezvous to be <em>positional</em> rather than temporal, and that is the
/// lesson these tests were rewritten to carry. Firing a starting gun and trusting the scheduler gave
/// a suite that caught the deferred-transaction mutant on an idle machine and waved it through under
/// a loaded one: the continuations drained near-serially, the winner committed before the losers
/// reached their <c>SELECT</c>, every loser refused at the cheap pre-lock check, and the race simply
/// never happened. A concurrency test that only races when the machine is idle passes for the wrong
/// reason. Every attempt is now parked at a known point <em>in the code</em> —
/// <see cref="CountingPasswordHasher.OnHash"/>, which runs after the pre-lock validity read and
/// before <c>BEGIN IMMEDIATE</c> — and released only once all of them are there.
/// </para>
/// </remarks>
public sealed class InvitationRedemptionConcurrencyTests : IDisposable
{
    private const int ConcurrentAttempts = 8;
    private const string Password = "a good long passphrase";
    private const bool AsMember = false;

    /// <summary>How long an attempt waits for the others to reach the starting line.</summary>
    /// <remarks>
    /// Generous, because it is a deadlock guard rather than a timing assertion: overrunning it means
    /// the rendezvous never formed, which then fails loudly instead of hanging the suite.
    /// </remarks>
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a revocation holds its decision open while a redemption tries to land inside it.
    /// </summary>
    /// <remarks>
    /// Against the correct implementation this always elapses — the redemption is blocked on
    /// <c>BEGIN IMMEDIATE</c> and cannot land at all, which is the property being asserted. Against
    /// a check-then-act revocation the redemption commits immediately and the wait returns at once,
    /// so the half second is only ever paid on the green path.
    /// </remarks>
    private static readonly TimeSpan ClosingWindow = TimeSpan.FromMilliseconds(500);

    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SecretTokenGenerator _tokenGenerator = new();
    private readonly int _minimumWorkerThreads;
    private readonly int _minimumCompletionPortThreads;

    public InvitationRedemptionConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"zerowiki-redeem-{Guid.NewGuid():n}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = NewContext();
        db.Database.Migrate();

        // An attempt resumes on a pool thread after every await, and the pool grows by roughly one
        // thread per 500 ms when starved — which the rest of the suite running in parallel routinely
        // does. The rendezvous below is positional, so starvation cannot make these tests pass for
        // the wrong reason; it can only make them time out. Raising the floor removes that.
        ThreadPool.GetMinThreads(out _minimumWorkerThreads, out _minimumCompletionPortThreads);
        ThreadPool.SetMinThreads(_minimumWorkerThreads + (ConcurrentAttempts * 2), _minimumCompletionPortThreads);
    }

    public void Dispose()
    {
        ThreadPool.SetMinThreads(_minimumWorkerThreads, _minimumCompletionPortThreads);
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_redemptions_of_one_invitation_create_exactly_one_account()
    {
        // "Single-use" is a concurrency requirement, the same one "exactly one administrator" was.
        // A read-then-write cannot satisfy it on SQLite: the read takes no write lock, so every
        // caller observes the invitation unredeemed and every insert succeeds.
        var issuer = await AddAccountAsync("alice");
        var issued = await IssueAsync(issuer.Id);

        using var atTheStartingLine = new CountdownEvent(ConcurrentAttempts);

        // Every attempt has its own DbContext, and therefore its own connection — eight independent
        // writers, not one connection used eight times. Distinct usernames, so a duplicate name can
        // never be what refuses them.
        var attempts = Enumerable.Range(0, ConcurrentAttempts).Select(i => OnItsOwnThread(async () =>
        {
            var hasher = new CountingPasswordHasher
            {
                // The one point at which every attempt has read the invitation and found it
                // redeemable, and none of them has taken the lock. Holding all eight here is what
                // makes the race exist rather than depend on how the scheduler feels.
                OnHash = () =>
                {
                    atTheStartingLine.Signal();
                    Assert.True(
                        atTheStartingLine.Wait(Rendezvous),
                        "Not every redemption reached the point before the write lock.");
                },
            };

            await using var db = NewContext();

            return await NewService(db, hasher).RedeemAsync(issued.Token, $"invitee{i}", Password);
        })).ToArray();

        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(o => o == InvitationRedemption.Redeemed));
        Assert.Equal(
            ConcurrentAttempts - 1,
            outcomes.Count(o => o == InvitationRedemption.AlreadyRedeemed));

        await using var verify = NewContext();
        Assert.Equal(2, await verify.Accounts.CountAsync());
        Assert.NotNull((await verify.Invitations.AsNoTracking().SingleAsync()).RedeemedAt);
    }

    [Fact]
    public async Task A_revocation_cannot_commit_over_a_redemption_that_lands_while_it_is_deciding()
    {
        // Reviewer note N2 from Block 4a, pinned at the exact seam rather than by racing and hoping.
        // Revocation reads the row, tests RedeemedAt, then writes — and reads the clock in between.
        // That clock read is where a redemption used to be able to land, leaving a row carrying
        // *both* timestamps and telling the revoker "revoked" about an invitation that had already
        // created an account. A redemption is parked just before its own write lock, then released
        // into precisely that gap.
        var issuer = await AddAccountAsync("alice");
        var issued = await IssueAsync(issuer.Id);

        using var parkedBeforeTheLock = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var hasher = new CountingPasswordHasher
        {
            OnHash = () =>
            {
                parkedBeforeTheLock.Set();
                Assert.True(release.Wait(Rendezvous), "The revocation never reached its write.");
            },
        };

        var redemption = OnItsOwnThread(async () =>
        {
            await using var db = NewContext();

            return await NewService(db, hasher).RedeemAsync(issued.Token, "bob", Password);
        });

        Assert.True(
            parkedBeforeTheLock.Wait(Rendezvous),
            "The redemption never reached the point before the write lock.");

        var atTheSeam = new HookedTimeProvider(IssuedAt, () =>
        {
            release.Set();

            // The interleaving is asserted, not inferred. Without this the test's sensitivity rests
            // on an unstated assumption — that the clock read it hooks is still the one between the
            // revocation's read and its write. Add a harmless `timeProvider.GetUtcNow()` at the top
            // of RevokeAsync, the shape an ordinary refactor produces, and the hook fires before the
            // lock instead: the redemption commits early, the revocation reports AlreadyRedeemed,
            // and the outcome assertions below are satisfied on their *other* branch — so a genuine
            // check-then-act regression walks straight through a green suite. Requiring the
            // redemption to still be blocked here is what makes the seam moving a loud failure
            // instead of a silent loss of coverage.
            Assert.False(
                redemption.Wait(ClosingWindow),
                "The redemption completed while the revocation was still deciding. The hooked clock "
                + "read is no longer the one between the revocation's read and its write, so this "
                + "test is not exercising the interleaving it is named for.");
        });

        var revocation = await OnItsOwnThread(async () =>
        {
            await using var db = NewContext();

            return await NewService(db, time: atTheSeam).RevokeAsync(issuer.Id, AsMember, issued.Id);
        });

        // Belt: had the revocation refused before ever reaching its write, nothing would have
        // released the redemption.
        release.Set();
        var redeemed = await redemption;

        await using var verify = NewContext();
        var row = await verify.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);

        Assert.False(
            row.RedeemedAt is not null && row.RevokedAt is not null,
            $"The invitation was recorded as both redeemed and revoked (redemption said {redeemed}, "
            + $"revocation said {revocation}).");

        // Exactly one of the two can win, and what each caller was told has to match the row.
        Assert.True(
            (redeemed == InvitationRedemption.Redeemed) ^ (revocation == InvitationRevocation.Revoked),
            $"Redemption said {redeemed} and revocation said {revocation}.");

        if (redeemed == InvitationRedemption.Redeemed)
        {
            Assert.NotNull(row.RedeemedAt);
            Assert.Equal(InvitationRevocation.AlreadyRedeemed, revocation);
        }
        else
        {
            Assert.Null(row.RedeemedAt);
            Assert.Equal(InvitationRedemption.Revoked, redeemed);
            Assert.NotNull(row.RevokedAt);
        }
    }

    [Fact]
    public async Task The_password_is_hashed_before_the_write_lock_is_taken()
    {
        // Argon2id costs ~93 ms at 64 MiB; deriving it inside SQLite's single write lock would
        // serialise every other writer in the process behind a CPU burn. Asserted by looking at the
        // lock while a hash is in flight — a wall-clock measurement would only say it was slow.
        var issuer = await AddAccountAsync("alice");
        var issued = await IssueAsync(issuer.Id);

        var probed = false;
        Exception? probeFailure = null;

        var hasher = new CountingPasswordHasher
        {
            OnHash = () =>
            {
                probed = true;

                try
                {
                    using var probe = new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = _databasePath,

                            // Seconds. Long enough not to trip on scheduling noise, short enough
                            // that a genuinely held lock fails the test rather than hanging it.
                            DefaultTimeout = 2,
                        }.ToString());
                    probe.Open();

                    // BEGIN IMMEDIATE, which is exactly what a competing writer would issue.
                    using var contending = probe.BeginTransaction(deferred: false);
                    contending.Rollback();
                }
                catch (Exception exception)
                {
                    probeFailure = exception;
                }
            },
        };

        await using var db = NewContext();
        Assert.Equal(
            InvitationRedemption.Redeemed,
            await NewService(db, hasher).RedeemAsync(issued.Token, "bob", Password));

        Assert.True(probed, "The password was never hashed, so this asserted nothing.");
        Assert.Null(probeFailure);
    }

    /// <summary>Starts work on a thread of its own rather than borrowing one from the pool.</summary>
    /// <remarks>
    /// Only the entry point is dedicated — continuations after an await still resume on the pool —
    /// but it is enough that an attempt cannot be stopped from <em>starting</em> by a pool the rest
    /// of the suite has saturated. The constructor's thread floor covers the continuations.
    /// </remarks>
    private static Task<T> OnItsOwnThread<T>(Func<Task<T>> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private InvitationService NewService(
        IdentityDbContext db,
        IPasswordHasher? passwordHasher = null,
        TimeProvider? time = null) =>
        new(
            db,
            _tokenGenerator,
            passwordHasher ?? new CountingPasswordHasher(),
            time ?? new FakeTimeProvider(IssuedAt),
            new CapturingLoggerProvider().CreateLogger<InvitationService>());

    private async Task<IssuedInvitation> IssueAsync(Guid issuerAccountId)
    {
        await using var db = NewContext();

        return await NewService(db).IssueAsync(issuerAccountId);
    }

    private async Task<Account> AddAccountAsync(string username)
    {
        await using var db = NewContext();

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "$argon2id$stub",
            DisplayName = username,
            CreatedAt = IssuedAt,
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        return account;
    }

    private IdentityDbContext NewContext() => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>A fixed clock that runs a callback the first time it is read.</summary>
    /// <remarks>
    /// <see cref="InvitationService.RevokeAsync"/> reads the clock exactly once, between deciding
    /// the invitation is revocable and writing that decision — so this is a seam into the middle of
    /// a method, which no amount of scheduling pressure applied from outside could reach.
    /// </remarks>
    private sealed class HookedTimeProvider(DateTimeOffset now, Action onFirstRead) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                onFirstRead();
            }

            return now;
        }
    }
}
