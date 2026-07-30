using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;

namespace ZeroWiki.Tests.Data;

/// <summary>
/// Pins the AD7 timestamp representation: every <see cref="DateTimeOffset"/> is stored as a
/// fixed-width ISO-8601 UTC string, so comparison and ordering happen in SQL and mean what
/// they say. Runs against the real migration on in-memory SQLite.
/// </summary>
public sealed class DateTimeOffsetStorageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;

    public DateTimeOffsetStorageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = NewContext();
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Timestamp_is_stored_as_a_fixed_width_iso8601_utc_string()
    {
        _db.Accounts.Add(NewAccount("alice", new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero)));
        await _db.SaveChangesAsync();

        var stored = await TextAsync("SELECT CreatedAt FROM Accounts");

        Assert.Equal("2026-07-25T13:00:00.0000000Z", stored);
        Assert.Equal(28, stored.Length);
    }

    [Fact]
    public async Task Non_utc_input_is_normalised_to_the_same_instant()
    {
        // 18:00+05:00 is 13:00Z. The stored form must be the instant, not the local reading —
        // this is precisely what DateTimeOffsetToBinaryConverter fails to do.
        var written = new DateTimeOffset(2026, 7, 25, 18, 0, 0, TimeSpan.FromHours(5));
        _db.Accounts.Add(NewAccount("alice", written));
        await _db.SaveChangesAsync();

        Assert.Equal("2026-07-25T13:00:00.0000000Z", await TextAsync("SELECT CreatedAt FROM Accounts"));

        await using var reader = NewContext();
        var loaded = await reader.Accounts.SingleAsync();

        Assert.Equal(written, loaded.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero), loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
    }

    [Fact]
    public async Task Expired_invitation_is_excluded_by_a_predicate_evaluated_in_sql()
    {
        var admin = NewAccount("admin", new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));
        _db.Accounts.Add(admin);

        // Expires 11:00Z, written with a +05:00 offset — the exact shape the built-in binary
        // converter admitted as unexpired against a 12:00Z "now".
        var expired = NewInvitation(admin.Id, new DateTimeOffset(2026, 7, 25, 16, 0, 0, TimeSpan.FromHours(5)));
        var live = NewInvitation(admin.Id, new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero));
        _db.Invitations.AddRange(expired, live);
        await _db.SaveChangesAsync();

        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        await using var reader = NewContext();
        var query = reader.Invitations.AsNoTracking().Where(i => i.ExpiresAt > now);

        // ToQueryString only succeeds for a fully translated query, and the comparison has to
        // appear in the WHERE clause — so this cannot pass on a lucky client-side filter.
        var sql = query.ToQueryString();
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("\"ExpiresAt\" > ", sql, StringComparison.Ordinal);

        var unexpired = await query.ToListAsync();

        Assert.Equal(live.Id, Assert.Single(unexpired).Id);
    }

    [Fact]
    public async Task Live_invitation_written_with_a_negative_offset_survives_the_sql_predicate()
    {
        // The fail-closed mirror of the test above: expiring at 09:00-05:00 (= 14:00Z) is still
        // live against a 12:00Z "now", but a representation that preserves the offset compares
        // "T09" < "T12" and silently drops it.
        var admin = NewAccount("admin", new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));
        _db.Accounts.Add(admin);

        var live = NewInvitation(admin.Id, new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.FromHours(-5)));
        _db.Invitations.Add(live);
        await _db.SaveChangesAsync();

        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        await using var reader = NewContext();
        var query = reader.Invitations.AsNoTracking().Where(i => i.ExpiresAt > now);

        Assert.Contains("\"ExpiresAt\" > ", query.ToQueryString(), StringComparison.Ordinal);
        Assert.Equal(live.Id, Assert.Single(await query.ToListAsync()).Id);
    }

    [Fact]
    public async Task Timestamps_order_chronologically_in_sql_across_the_whole_range()
    {
        var admin = NewAccount("admin", DateTimeOffset.UnixEpoch);
        _db.Accounts.Add(admin);

        var earliest = NewInvitation(admin.Id, DateTimeOffset.MinValue);
        var latest = NewInvitation(admin.Id, DateTimeOffset.MaxValue);

        // Two adversarial pairs, so that ordering here can only pass on a representation that
        // is both UTC-normalised and fixed-width:
        //   * whole vs sub-second — '.' (0x2E) sorts before 'Z' (0x5A), so dropping trailing
        //     fractional digits inverts them;
        //   * 13:00Z vs 09:00-05:00 (= 14:00Z) — a *negative* offset, so preserving the offset
        //     instead of normalising inverts them too ("T09" < "T13" lexicographically).
        var wholeSecond = NewInvitation(admin.Id, new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero));
        var subSecond = NewInvitation(
            admin.Id,
            new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero).AddTicks(5_000_000));
        var negativeOffset = NewInvitation(
            admin.Id,
            new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.FromHours(-5)));

        _db.Invitations.AddRange(latest, negativeOffset, earliest, subSecond, wholeSecond);
        await _db.SaveChangesAsync();

        await using var reader = NewContext();
        var query = reader.Invitations.AsNoTracking().OrderBy(i => i.ExpiresAt).Select(i => i.Id);

        Assert.Contains("ORDER BY", query.ToQueryString(), StringComparison.Ordinal);

        // Year 1, 13:00:00.0Z, 13:00:00.5Z, 14:00Z (written as 09:00-05:00), year 9999.
        Assert.Equal(
            new[] { earliest.Id, wholeSecond.Id, subSecond.Id, negativeOffset.Id, latest.Id },
            await query.ToArrayAsync());
    }

    [Fact]
    public async Task Null_timestamp_stays_null_and_is_still_queryable_in_sql()
    {
        var account = NewAccount("alice", new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));
        var live = NewToken(account.Id, "live", revokedAt: null);
        account.GitTokens.Add(live);
        account.GitTokens.Add(
            NewToken(account.Id, "revoked", new DateTimeOffset(2026, 7, 25, 11, 0, 0, TimeSpan.Zero)));
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM GitTokens WHERE RevokedAt IS NULL"));

        await using var reader = NewContext();
        var query = reader.GitTokens.AsNoTracking().Where(t => t.RevokedAt == null);
        Assert.Contains("IS NULL", query.ToQueryString(), StringComparison.Ordinal);

        var loaded = Assert.Single(await query.ToListAsync());
        Assert.Equal(live.Id, loaded.Id);
        Assert.Null(loaded.RevokedAt);
    }

    [Fact]
    public async Task Nullable_timestamp_is_also_stored_in_the_fixed_width_form()
    {
        // The convention has to reach DateTimeOffset? too, not only DateTimeOffset.
        var account = NewAccount("alice", new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));
        account.GitTokens.Add(
            NewToken(account.Id, "revoked", new DateTimeOffset(2026, 7, 25, 16, 0, 0, TimeSpan.FromHours(5))));
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        Assert.Equal("2026-07-25T11:00:00.0000000Z", await TextAsync("SELECT RevokedAt FROM GitTokens"));
    }

    private IdentityDbContext NewContext() => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);

    private static Account NewAccount(string username, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "$argon2id$stub",
        DisplayName = username,
        CreatedAt = createdAt,
    };

    private static Invitation NewInvitation(Guid issuerAccountId, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("n"),
        IssuerAccountId = issuerAccountId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        ExpiresAt = expiresAt,
    };

    private static GitToken NewToken(Guid accountId, string tokenHash, DateTimeOffset? revokedAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TokenHash = tokenHash,
        CreatedAt = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero),
        RevokedAt = revokedAt,
    };

    private async Task<string> TextAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
}
