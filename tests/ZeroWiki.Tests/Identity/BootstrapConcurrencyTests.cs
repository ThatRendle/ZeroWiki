using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Proves "exactly one administrator" holds under genuine concurrency. This needs a
/// file-backed database: the shared in-memory pattern used elsewhere is a single connection,
/// which cannot exhibit the two-writers race at all.
/// </summary>
/// <remarks>
/// <para>
/// The rendezvous is <em>positional</em> — every attempt is parked at a known point <em>in the
/// code</em> and released only once all of them are there. This class previously fired a
/// <c>TaskCompletionSource</c> starting gun and trusted the scheduler, which is the arrangement
/// <see cref="InvitationRedemptionConcurrencyTests"/> was rewritten away from for a recorded reason:
/// it caught the deferred-transaction mutant on an idle machine and waved it through under a loaded
/// one. Measured here before the conversion, under the full parallel suite, that mutant survived 6
/// runs in 13. A concurrency test that only races when the machine is idle passes for the wrong
/// reason.
/// </para>
/// <para>
/// Two seams are needed, not one, because bootstrap's critical section is short enough for a winner
/// to finish inside the gap the first seam leaves:
/// </para>
/// <list type="number">
/// <item>
/// <see cref="CountingPasswordHasher.OnHash"/>, which <see cref="BootstrapService"/> reaches after
/// its cheap pre-lock read and before <c>BEGIN IMMEDIATE</c>. Holding all eight here is what makes
/// every attempt observe an unbootstrapped store — without it a straggler arrives after the winner
/// has committed, refuses at the cheap check, and never contends at all.
/// </item>
/// <item>
/// <see cref="PausingTimeProvider"/>, on the clock read <see cref="BootstrapService"/> makes
/// <em>inside</em> the transaction, between the read that decides and the write that acts. Widening
/// that gap gives the other seven time to reach their own decisive read. Against the correct
/// implementation they cannot: they are blocked on <c>BEGIN IMMEDIATE</c> and see the committed row
/// when they finally get in, so the outcome is unchanged and only the wall clock moves. Against a
/// deferred transaction nothing blocks them, all eight read an empty store, and the race the
/// assertions are about actually happens.
/// </item>
/// </list>
/// <para>
/// The pause is a widening, not an assertion — no asserted property depends on its length, only the
/// reliability with which a broken implementation is caught.
/// </para>
/// </remarks>
public sealed class BootstrapConcurrencyTests : IDisposable
{
    private const int ConcurrentAttempts = 8;
    private const string Password = "a good long passphrase";

    private static readonly DateTimeOffset Now = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    /// <summary>How long an attempt waits for the others to reach the starting line.</summary>
    /// <remarks>
    /// Generous, because it is a deadlock guard rather than a timing assertion: overrunning it means
    /// the rendezvous never formed, which then fails loudly instead of hanging the suite.
    /// </remarks>
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);

    /// <summary>How long the attempt holding the write lock waits before it writes.</summary>
    private static readonly TimeSpan WriteWindow = TimeSpan.FromMilliseconds(500);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly int _minimumWorkerThreads;
    private readonly int _minimumCompletionPortThreads;

    public BootstrapConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"zerowiki-bootstrap-{Guid.NewGuid():n}.db");
        _connectionString = TestDatabase.ConnectionStringFor(_databasePath);

        using var db = NewContext();
        db.Database.Migrate();

        // The barrier below parks eight threads at once, and the pool grows by roughly one thread
        // per 500 ms when starved — which the rest of the suite running in parallel routinely does.
        // The rendezvous is positional, so starvation cannot make this pass for the wrong reason; it
        // can only make it time out. Raising the floor removes that.
        ThreadPool.GetMinThreads(out _minimumWorkerThreads, out _minimumCompletionPortThreads);
        ThreadPool.SetMinThreads(_minimumWorkerThreads + (ConcurrentAttempts * 2), _minimumCompletionPortThreads);
    }

    public void Dispose()
    {
        ThreadPool.SetMinThreads(_minimumWorkerThreads, _minimumCompletionPortThreads);
        TestDatabase.Delete(_databasePath);
    }

    [Fact]
    public async Task Concurrent_bootstrap_attempts_create_exactly_one_administrator()
    {
        using var atTheStartingLine = new CountdownEvent(ConcurrentAttempts);
        var arrived = 0;
        var releasedWith = new int[ConcurrentAttempts];
        string? seamMoved = null;

        // Every attempt has its own DbContext, and therefore its own connection — eight independent
        // writers, not one connection used eight times. The clock is shared, because the point of it
        // is to widen one gap once, whoever reaches it.
        var clock = new HookedTimeProvider(Now, () =>
        {
            // The seam's *position* is asserted, not inferred, and this is the assertion the first
            // version of this class was missing. Everything below rests on an unstated assumption —
            // that the clock read being hooked is still the one between the deciding read and the
            // write, taken with the lock already held. Hoist `timeProvider.GetUtcNow()` to the top of
            // CreateFirstAdministratorAsync and use the captured value at the insert — one line,
            // semantics-preserving, the shape an ordinary tidy-up produces — and this hook fires
            // before the transaction instead. The widening then happens where nothing is contended,
            // the race stops forming, and the deferred-transaction mutant goes back to surviving
            // about half the time *with every test still green*. Measured: 6 kills in 12 runs, versus
            // 13 in 13 with the seam where it belongs. Requiring the write lock to be held here is
            // what makes the seam moving a loud failure instead of a silent loss of coverage.
            if (!WriteLockIsHeld())
            {
                seamMoved =
                    "The store's write lock was not held when the clock was read, so the hooked read "
                    + "is no longer the one between the deciding read and the write. This test is not "
                    + "widening the window it is named for, and the concurrency it asserts is no "
                    + "longer being exercised.";
            }

            // Deliberately outside the check above: on a deferred transaction the probe returns at
            // once, and the window still has to be widened or the mutant this test exists to catch
            // goes back to being caught half the time.
            Thread.Sleep(WriteWindow);
        });

        var attempts = Enumerable.Range(0, ConcurrentAttempts).Select(i => OnItsOwnThread(async () =>
        {
            var hasher = new CountingPasswordHasher
            {
                // The one point at which every attempt has read the store and found it
                // unbootstrapped, and none of them has taken the lock.
                OnHash = () =>
                {
                    Interlocked.Increment(ref arrived);
                    atTheStartingLine.Signal();
                    Assert.True(
                        atTheStartingLine.Wait(Rendezvous),
                        "Not every bootstrap attempt reached the point before the write lock.");

                    releasedWith[i] = Volatile.Read(ref arrived);
                },
            };

            await using var db = NewContext();

            return await new BootstrapService(db, hasher, clock)
                .CreateFirstAdministratorAsync($"admin{i}", Password);
        })).ToArray();

        var outcomes = await Task.WhenAll(attempts);

        // Seam 2. Reported here rather than thrown from the hook so the failure names the cause
        // instead of surfacing as seven attempts timing out on a rendezvous nobody completed.
        Assert.True(seamMoved is null, seamMoved);

        // Seam 1, asserted rather than assumed. Every attempt records how many had arrived at the
        // moment it was let go; if the mechanism were letting anyone through early, one of these
        // would be short. Without this the test's sensitivity rests on CountdownEvent behaving as
        // documented, which is the sort of thing that should be measured once rather than believed.
        Assert.All(releasedWith, count => Assert.Equal(ConcurrentAttempts, count));

        Assert.Equal(1, outcomes.Count(o => o == BootstrapOutcome.Created));
        Assert.Equal(ConcurrentAttempts - 1, outcomes.Count(o => o == BootstrapOutcome.AlreadyBootstrapped));

        await using var verify = NewContext();
        var account = Assert.Single(await verify.Accounts.AsNoTracking().ToListAsync());
        Assert.True(account.IsAdministrator);
    }

    [Fact]
    public async Task Concurrent_attempts_against_an_already_populated_store_create_nothing()
    {
        await using (var seed = NewContext())
        {
            seed.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = "existing",
                PasswordHash = "$argon2id$stub",
                DisplayName = "existing",
                CreatedAt = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero),
            });
            await seed.SaveChangesAsync();
        }

        // No positional barrier here, and that is not an oversight: against a populated store every
        // attempt refuses at the cheap pre-lock read, so none of them ever reaches the seam the test
        // above parks at. Waiting for eight arrivals that cannot happen would hang. What this test
        // is about is that a populated store refuses everyone, which the hashers below assert
        // directly — nobody got far enough to derive a key.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashers = new CountingPasswordHasher[ConcurrentAttempts];

        var attempts = Enumerable.Range(0, ConcurrentAttempts).Select(async i =>
        {
            hashers[i] = new CountingPasswordHasher();

            await using var db = NewContext();
            var service = new BootstrapService(db, hashers[i], new FakeTimeProvider(Now));

            await release.Task;

            return await service.CreateFirstAdministratorAsync($"intruder{i}", Password);
        }).ToArray();

        release.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.All(outcomes, o => Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, o));

        // BL1's property on this path: a refusal costs no key derivation.
        Assert.All(hashers, hasher => Assert.Empty(hasher.Derivations));

        await using var verify = NewContext();
        Assert.Equal("existing", Assert.Single(await verify.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    /// <summary>Starts work on a thread of its own rather than borrowing one from the pool.</summary>
    /// <remarks>
    /// The barrier blocks its thread, so eight attempts must be able to be in flight at once. Only
    /// the entry point is dedicated — continuations after an await still resume on the pool — but it
    /// is enough that an attempt cannot be stopped from <em>starting</em> by a pool the rest of the
    /// suite has saturated. The constructor's thread floor covers the continuations.
    /// </remarks>
    private static Task<T> OnItsOwnThread<T>(Func<Task<T>> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private IdentityDbContext NewContext() => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>Whether some other connection currently holds the store's write lock.</summary>
    /// <remarks>
    /// Asked by issuing exactly what a competing writer would — <c>BEGIN IMMEDIATE</c> — from a
    /// connection of its own. Being refused is the answer; a wall-clock measurement would only say
    /// the attempt was slow. The same instrument, in the opposite direction, is what
    /// <see cref="InvitationRedemptionConcurrencyTests"/> uses to assert a hash happens *outside*
    /// the lock.
    /// </remarks>
    private bool WriteLockIsHeld()
    {
        try
        {
            using var probe = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,

                    // Seconds, and the minimum the API accepts. Long enough not to trip on
                    // scheduling noise, short enough that this cannot hang the suite.
                    DefaultTimeout = 1,
                    Pooling = false,
                }.ToString());
            probe.Open();

            using var contending = probe.BeginTransaction(deferred: false);
            contending.Rollback();

            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    /// <summary>A fixed clock that runs a callback the first time it is read.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="BootstrapService"/> reads the clock once, inside the transaction, after the read
    /// that decides whether to insert and before the insert itself — so this reaches into the middle
    /// of a method, which no amount of scheduling pressure applied from outside could.
    /// </para>
    /// <para>
    /// Deliberately not shared with <see cref="InvitationRedemptionConcurrencyTests"/>'s equivalent.
    /// That class's mutation coverage is measured, and promoting its private double into a shared
    /// type would put a change into it for no gain here.
    /// </para>
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
