using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Covers the two spec scenarios for the first-administrator bootstrap and the per-request
/// inertness gate, against the real migration on in-memory SQLite.
/// </summary>
public sealed class BootstrapServiceTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly FakeTimeProvider _time = new(CreatedAt);
    private readonly Argon2idPasswordHasher _hasher = new();
    private readonly BootstrapService _service;

    public BootstrapServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _service = new BootstrapService(_db, _hasher, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Bootstrap_on_an_empty_store_creates_exactly_one_administrator()
    {
        Assert.True(await _service.IsAvailableAsync());

        var outcome = await _service.CreateFirstAdministratorAsync("alice", "a good long passphrase");

        Assert.Equal(BootstrapOutcome.Created, outcome);

        var account = Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync());
        Assert.Equal("alice", account.Username);
        Assert.True(account.IsAdministrator);
        Assert.Equal(CreatedAt, account.CreatedAt);
        Assert.Equal("alice", account.DisplayName);
    }

    [Fact]
    public async Task Created_administrator_can_be_verified_with_the_submitted_password()
    {
        await _service.CreateFirstAdministratorAsync("alice", "a good long passphrase");

        var account = Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync());

        Assert.True(_hasher.Verify("a good long passphrase", account.PasswordHash));
        Assert.False(_hasher.Verify("the wrong passphrase", account.PasswordHash));
        Assert.DoesNotContain("a good long passphrase", account.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_creates_no_account_once_one_already_exists()
    {
        await AddAccountAsync("existing");

        Assert.False(await _service.IsAvailableAsync());

        var outcome = await _service.CreateFirstAdministratorAsync("intruder", "a good long passphrase");

        Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, outcome);
        Assert.Equal("existing", Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    [Fact]
    public async Task Bootstrap_is_inert_against_a_non_administrator_account_too()
    {
        // "Once any account exists" — not "once an administrator exists". An invited member
        // must close the path just as firmly.
        await AddAccountAsync("member", isAdministrator: false);

        Assert.False(await _service.IsAvailableAsync());
        Assert.Equal(
            BootstrapOutcome.AlreadyBootstrapped,
            await _service.CreateFirstAdministratorAsync("intruder", "a good long passphrase"));
    }

    [Fact]
    public async Task Gate_closes_the_moment_an_account_appears_without_a_restart()
    {
        // The same service instance must answer differently before and after, which is only
        // true if the gate is re-evaluated against the store on every call. A gate that cached
        // its answer at startup would keep this open for the life of the process.
        Assert.True(await _service.IsAvailableAsync());

        await AddAccountAsync("someone");

        Assert.False(await _service.IsAvailableAsync());

        await _db.Accounts.ExecuteDeleteAsync();

        Assert.True(await _service.IsAvailableAsync());
    }

    [Fact]
    public async Task A_cancelled_bootstrap_leaves_no_administrator_behind()
    {
        // Pre-cancelled, not mid-flight. The first cancellable await here is the pre-filter
        // `AnyAsync` on an empty store (BootstrapService.cs:81), which throws before any
        // connection, transaction, or row is touched — so this proves the method is cancellable
        // at all and that an early cancel leaves nothing behind, and no more than that. It is
        // the *weakest* form of "leaves nothing behind": the transactional rollback between
        // SaveChangesAsync and CommitAsync (:124-125) is never entered, so this test cannot fail
        // if that rollback were broken. See the mid-flight test below for the property this one
        // cannot reach.
        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CreateFirstAdministratorAsync("alice", "a good long passphrase", cancellationToken));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        // The store, not the return value: a cancelled call throws and has no return value to
        // assert against at all. The absence of the row is the only evidence that means anything.
        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_cancellation_between_the_write_and_the_commit_still_rolls_back()
    {
        // Reaches the window the pre-cancelled test above cannot: cancel the token only once
        // SaveChangesAsync has finished writing into the still-uncommitted transaction, so the
        // token is live through the whole first check-then-act and only goes cancelled right
        // before CommitAsync (BootstrapService.cs:124-125) runs. If that rollback were broken —
        // if the row survived a cancelled commit — this test, unlike the pre-cancelled one,
        // would see it and fail.
        var cancellationTokenSource = new CancellationTokenSource();

        await using var interceptingDb = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new CancelAfterSaveInterceptor(cancellationTokenSource))
                .Options);
        var interceptingService = new BootstrapService(interceptingDb, _hasher, _time);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interceptingService.CreateFirstAdministratorAsync(
                "alice", "a good long passphrase", cancellationTokenSource.Token));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);
        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_cancelled_availability_check_throws_rather_than_reporting_the_store_empty()
    {
        // Empty store is load-bearing (design.md Risks): IsAvailableAsync is !AnyAsync, so
        // against an empty store the honest, uncancelled answer is `true` — the fail-open value.
        // Asserting the cancelled call throws therefore distinguishes throw from fail-open.
        // Against a populated store the same assertion would only distinguish throw from
        // fail-closed (`false`), which the requirement does not forbid and would pass while
        // proving nothing about the one fail-open path in this change.
        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.IsAvailableAsync(cancellationToken));

        // Confirms the throw comes from the token being honoured (EF's AnyAsync respecting
        // cancellation), not assumed from some other failure that happened to also throw.
        Assert.True(thrown.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Second_bootstrap_attempt_after_a_successful_one_is_refused()
    {
        Assert.Equal(
            BootstrapOutcome.Created,
            await _service.CreateFirstAdministratorAsync("alice", "a good long passphrase"));

        Assert.Equal(
            BootstrapOutcome.AlreadyBootstrapped,
            await _service.CreateFirstAdministratorAsync("bob", "another good passphrase"));

        Assert.Equal("alice", Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    [Fact]
    public async Task Username_is_trimmed()
    {
        await _service.CreateFirstAdministratorAsync("  alice  ", "a good long passphrase");

        Assert.Equal("alice", Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_username_is_rejected_before_anything_is_written(string username)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFirstAdministratorAsync(username, "a good long passphrase"));

        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("colon:name")]
    [InlineData("has space")]
    [InlineData("café")]
    [InlineData("___")]
    [InlineData("admin\tx")]
    public async Task Username_outside_the_permitted_charset_is_rejected_by_the_service_itself(string username)
    {
        // The web form validates too, but the invariant belongs to the store: §8 presents the
        // username as a Basic-auth userid, where a colon is structurally illegal. A caller that
        // is not the form must not be able to persist one.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFirstAdministratorAsync(username, "a good long passphrase"));

        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Overlong_username_is_rejected_by_the_service_itself()
    {
        var username = new string('a', 65);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFirstAdministratorAsync(username, "a good long passphrase"));

        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("a.b-c_1")]
    [InlineData("_x_")]
    [InlineData("  admin  ")]
    // Trimmed first, so a pasted trailing newline is accepted as "admin" rather than refused.
    [InlineData("admin\n")]
    public async Task Username_within_the_permitted_charset_is_accepted_by_the_service(string username)
    {
        Assert.Equal(
            BootstrapOutcome.Created,
            await _service.CreateFirstAdministratorAsync(username, "a good long passphrase"));

        Assert.Equal(
            username.Trim(),
            Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task Password_below_the_minimum_length_is_rejected_by_the_service_itself(string password)
    {
        // The first administrator is minted with no invitation, no authentication and no audit
        // trail, and nothing in this system resets a password — so a weak one here is permanent.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFirstAdministratorAsync("alice", password));

        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_password_at_exactly_the_minimum_length_is_accepted()
    {
        var password = new string('p', CredentialPolicy.MinimumPasswordLength);

        Assert.Equal(
            BootstrapOutcome.Created,
            await _service.CreateFirstAdministratorAsync("alice", password));

        Assert.True(_hasher.Verify(password, Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).PasswordHash));
    }

    [Fact]
    public async Task Blank_password_is_rejected_before_anything_is_written()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateFirstAdministratorAsync("alice", string.Empty));

        Assert.Empty(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    private async Task AddAccountAsync(string username, bool isAdministrator = true)
    {
        _db.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "$argon2id$stub",
            DisplayName = username,
            IsAdministrator = isAdministrator,
            CreatedAt = CreatedAt,
        });

        await _db.SaveChangesAsync();
    }
}
