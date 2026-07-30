using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Covers credential verification: that it accepts only correct credentials, that every
/// rejection costs the same work, and that the reason is recoverable from the log but not from
/// the answer.
/// </summary>
public sealed class LoginServiceTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly Argon2idPasswordHasher _hasher = new();
    private readonly RecordingPasswordHasher _recorder;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly LoginService _service;

    public LoginServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _recorder = new RecordingPasswordHasher(_hasher);
        _service = new LoginService(_db, _recorder, _logs.CreateLogger<LoginService>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Correct_credentials_resolve_to_the_account()
    {
        var id = await AddAccountAsync(Username, _hasher.Hash(Password), isAdministrator: true);

        var account = await _service.VerifyCredentialsAsync(Username, Password);

        Assert.NotNull(account);
        Assert.Equal(id, account.Id);
        Assert.Equal(Username, account.Username);
        Assert.True(account.IsAdministrator);
    }

    [Fact]
    public async Task Username_is_matched_case_insensitively_as_the_store_stores_it()
    {
        await AddAccountAsync(Username, _hasher.Hash(Password));

        var account = await _service.VerifyCredentialsAsync("ALICE", Password);

        Assert.NotNull(account);
        Assert.Equal(Username, account.Username);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await AddAccountAsync(Username, _hasher.Hash(Password));

        Assert.Null(await _service.VerifyCredentialsAsync(Username, "the wrong passphrase"));
    }

    [Fact]
    public async Task Unknown_username_is_rejected()
    {
        await AddAccountAsync(Username, _hasher.Hash(Password));

        Assert.Null(await _service.VerifyCredentialsAsync("nobody", Password));
    }

    [Fact]
    public async Task Unusable_stored_hash_is_rejected_rather_than_throwing()
    {
        await AddAccountAsync(Username, "not-a-hash");

        Assert.Null(await _service.VerifyCredentialsAsync(Username, Password));
    }

    [Fact]
    public async Task Every_rejection_performs_exactly_one_verification()
    {
        // The property that makes an unknown username cost what a known one costs. Asserting the
        // work rather than the clock: no wall-clock comparison to go flaky under load.
        await AddAccountAsync(Username, _hasher.Hash(Password));
        await AddAccountAsync("broken", "not-a-hash");

        Assert.Null(await _service.VerifyCredentialsAsync("nobody", Password));
        Assert.Null(await _service.VerifyCredentialsAsync("broken", Password));
        Assert.Null(await _service.VerifyCredentialsAsync(Username, "the wrong passphrase"));
        Assert.NotNull(await _service.VerifyCredentialsAsync(Username, Password));

        Assert.Equal(4, _recorder.Verifications.Count);

        // Both arguments, not just the stored hash: verifying an *empty* password against the
        // right dummy would still be one call against the right constant, and would cost
        // nothing — a free miss path, which is the oracle this whole design exists to close.
        Assert.Collection(
            _recorder.Verifications.Select(v => v.Password),
            unknownUsername => Assert.Equal(Password, unknownUsername),
            unusableHash => Assert.Equal(Password, unusableHash),
            wrongPassword => Assert.Equal("the wrong passphrase", wrongPassword),
            correct => Assert.Equal(Password, correct));

        // The two paths with no usable stored hash verify against the same constant dummy, and
        // it is a real hash carrying the live cost parameters — not a short-circuit.
        Assert.Equal(_recorder.VerifiedAgainst[0], _recorder.VerifiedAgainst[1]);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", _recorder.VerifiedAgainst[0], StringComparison.Ordinal);
        Assert.True(_hasher.CanVerify(_recorder.VerifiedAgainst[0]));

        // The two paths with a usable stored hash verify against that account's own hash.
        Assert.Equal(_recorder.VerifiedAgainst[2], _recorder.VerifiedAgainst[3]);
        Assert.NotEqual(_recorder.VerifiedAgainst[0], _recorder.VerifiedAgainst[2]);
    }

    [Fact]
    public async Task A_rejected_login_verifies_the_password_that_was_actually_submitted()
    {
        // The mutation this closes: one verification, against the correct dummy constant, but of
        // string.Empty — indistinguishable from correct behaviour unless the password is recorded.
        await _service.VerifyCredentialsAsync("nobody", Password);

        var verification = Assert.Single(_recorder.Verifications);
        Assert.Equal(Password, verification.Password);
        Assert.NotEmpty(verification.Password);
    }

    [Fact]
    public async Task The_dummy_hash_is_a_constant_and_is_not_derived_per_request()
    {
        // Deriving a throwaway hash per miss would cost a hash *and* a verify, making the miss
        // path slower than the hit path — the same oracle, inverted.
        await _service.VerifyCredentialsAsync("nobody", Password);
        await _service.VerifyCredentialsAsync("nobody-else", "another passphrase");

        Assert.Equal(_recorder.VerifiedAgainst[0], _recorder.VerifiedAgainst[1]);
    }

    [Fact]
    public async Task No_password_or_hash_is_ever_written_to_the_log()
    {
        var storedHash = _hasher.Hash(Password);
        await AddAccountAsync(Username, storedHash);

        await _service.VerifyCredentialsAsync(Username, Password);
        await _service.VerifyCredentialsAsync(Username, "the wrong passphrase");
        await _service.VerifyCredentialsAsync("nobody", Password);

        // Written, not Messages. Measured rather than assumed: a value carried by a log *scope*
        // (BeginScope) reaches a structured sink while appearing in no rendered message, so a
        // message-only sweep passes the full suite with a real leak live — see CapturingLoggerProvider.
        var log = string.Join('\n', _logs.Written);
        Assert.DoesNotContain(Password, log, StringComparison.Ordinal);
        Assert.DoesNotContain("the wrong passphrase", log, StringComparison.Ordinal);
        Assert.DoesNotContain(storedHash, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_three_rejections_are_distinguishable_in_the_log()
    {
        // Indistinguishable in the response, distinguishable to whoever has to answer "why can
        // alice not log in" — a corrupt stored hash is otherwise permanently silent.
        var brokenId = await AddAccountAsync("broken", "not-a-hash");
        var aliceId = await AddAccountAsync(Username, _hasher.Hash(Password));

        await _service.VerifyCredentialsAsync("nobody", Password);
        await _service.VerifyCredentialsAsync("broken", Password);
        await _service.VerifyCredentialsAsync(Username, "the wrong passphrase");

        Assert.Collection(
            _logs.Entries,
            unknown =>
            {
                Assert.Equal(LogLevel.Information, unknown.Level);
                Assert.Contains("no account with username", unknown.Message, StringComparison.Ordinal);
            },
            unusable =>
            {
                Assert.Equal(LogLevel.Error, unusable.Level);
                Assert.Contains("unusable", unusable.Message, StringComparison.Ordinal);
                Assert.Contains(brokenId.ToString(), unusable.Message, StringComparison.Ordinal);
            },
            wrongPassword =>
            {
                Assert.Equal(LogLevel.Information, wrongPassword.Level);
                Assert.Contains("wrong password", wrongPassword.Message, StringComparison.Ordinal);
                Assert.Contains(aliceId.ToString(), wrongPassword.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task A_corrupt_timestamp_column_does_not_break_login_for_that_account()
    {
        // The projection is what makes this true: materialising the entity would run the
        // timestamp converter and throw, turning this one account's login into a 500 while every
        // other failure returned the uniform rejection.
        var id = await AddAccountAsync(Username, _hasher.Hash(Password));
        Assert.Equal(1, await ExecuteAsync("UPDATE Accounts SET CreatedAt = 'not-a-timestamp'"));

        await using (var reader = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => reader.Accounts.ToListAsync());
        }

        var account = await _service.VerifyCredentialsAsync(Username, Password);

        Assert.NotNull(account);
        Assert.Equal(id, account.Id);
        Assert.Null(await _service.VerifyCredentialsAsync(Username, "the wrong passphrase"));
    }

    private async Task<Guid> AddAccountAsync(
        string username,
        string passwordHash,
        bool isAdministrator = false)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHash,
            DisplayName = username,
            IsAdministrator = isAdministrator,
            CreatedAt = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero),
        };

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return account.Id;
    }

    private async Task<int> ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
}
