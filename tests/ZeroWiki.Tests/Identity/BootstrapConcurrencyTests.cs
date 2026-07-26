using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Proves "exactly one administrator" holds under genuine concurrency. This needs a
/// file-backed database: the shared in-memory pattern used elsewhere is a single connection,
/// which cannot exhibit the two-writers race at all.
/// </summary>
public sealed class BootstrapConcurrencyTests : IDisposable
{
    private const int ConcurrentAttempts = 8;

    private readonly string _databasePath;
    private readonly string _connectionString;

    public BootstrapConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"zerowiki-bootstrap-{Guid.NewGuid():n}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = NewContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_bootstrap_attempts_create_exactly_one_administrator()
    {
        // Every attempt has its own DbContext, and therefore its own connection — this is two
        // (here, eight) independent writers, not one connection used twice.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, ConcurrentAttempts).Select(async i =>
        {
            await using var db = NewContext();
            var service = new BootstrapService(
                db,
                new StubPasswordHasher(),
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero)));

            // Line every attempt up on the same starting gun so they contend for the write lock
            // at effectively the same instant.
            await release.Task;

            return await service.CreateFirstAdministratorAsync($"admin{i}", "a good long passphrase");
        }).ToArray();

        release.SetResult();
        var outcomes = await Task.WhenAll(attempts);

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

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, ConcurrentAttempts).Select(async i =>
        {
            await using var db = NewContext();
            var service = new BootstrapService(
                db,
                new StubPasswordHasher(),
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero)));

            await release.Task;

            return await service.CreateFirstAdministratorAsync($"intruder{i}", "a good long passphrase");
        }).ToArray();

        release.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.All(outcomes, o => Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, o));

        await using var verify = NewContext();
        Assert.Equal("existing", Assert.Single(await verify.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    private IdentityDbContext NewContext() => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connectionString).Options);

    /// <summary>
    /// Stands in for Argon2id so the race window is decided by the transaction, not by ~100 ms
    /// of key derivation per attempt (which would also cost half a gigabyte across eight tasks).
    /// </summary>
    private sealed class StubPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => $"$stub${password.Length}";

        public bool Verify(string password, string? storedHash) => Hash(password) == storedHash;

        public bool CanVerify(string? storedHash) => storedHash?.StartsWith("$stub$", StringComparison.Ordinal) == true;
    }
}
