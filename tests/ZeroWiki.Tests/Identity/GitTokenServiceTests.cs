using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Exercises <see cref="GitTokenService"/> against the real EF Core migration on an
/// in-memory SQLite connection, so the token lifecycle is tested through the actual schema.
/// </summary>
public sealed class GitTokenServiceTests : IDisposable
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly FakeTimeProvider _time = new(IssuedAt);
    private readonly SecretTokenGenerator _tokenGenerator = new();
    private readonly GitTokenService _service;

    public GitTokenServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _service = new GitTokenService(_db, _tokenGenerator, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Issued_token_is_stored_only_as_a_hash()
    {
        var account = await AddAccountAsync("alice");

        var issued = await _service.IssueAsync(account.Id);

        var stored = await _db.GitTokens.AsNoTracking().SingleAsync(t => t.Id == issued.Id);

        Assert.Equal(_tokenGenerator.ComputeHash(issued.Token), stored.TokenHash);
        Assert.NotEqual(issued.Token, stored.TokenHash);
        Assert.Equal(IssuedAt, issued.CreatedAt);
        Assert.Null(stored.RevokedAt);

        // The plaintext must appear nowhere in the persisted row, not merely in a different column.
        Assert.DoesNotContain(issued.Token, await DumpGitTokenRowsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_token_resolves_to_its_owning_account()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        var aliceToken = await _service.IssueAsync(alice.Id);
        await _service.IssueAsync(bob.Id);

        var resolved = await _service.VerifyAsync("alice", aliceToken.Token);

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.Id);
        Assert.Equal("alice", resolved.Username);
    }

    [Fact]
    public async Task Username_comparison_is_case_insensitive()
    {
        var alice = await AddAccountAsync("alice");
        var aliceToken = await _service.IssueAsync(alice.Id);

        var resolved = await _service.VerifyAsync("ALICE", aliceToken.Token);

        Assert.NotNull(resolved);
        Assert.Equal(alice.Id, resolved.Id);
    }

    [Fact]
    public async Task A_token_does_not_authenticate_under_another_accounts_username()
    {
        var alice = await AddAccountAsync("alice");
        await AddAccountAsync("bob");
        var aliceToken = await _service.IssueAsync(alice.Id);

        Assert.Null(await _service.VerifyAsync("bob", aliceToken.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task Missing_or_unknown_token_resolves_to_nothing(string? presented)
    {
        var account = await AddAccountAsync("alice");
        await _service.IssueAsync(account.Id);

        Assert.Null(await _service.VerifyAsync("alice", presented));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_username_resolves_to_nothing(string? username)
    {
        var account = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(account.Id);

        Assert.Null(await _service.VerifyAsync(username, issued.Token));
    }

    [Fact]
    public async Task Revoked_token_no_longer_verifies()
    {
        var account = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(account.Id);
        Assert.NotNull(await _service.VerifyAsync("alice", issued.Token));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await _service.RevokeAsync(account.Id, issued.Id));

        Assert.Null(await _service.VerifyAsync("alice", issued.Token));

        var stored = await _db.GitTokens.AsNoTracking().SingleAsync(t => t.Id == issued.Id);
        Assert.Equal(IssuedAt.AddHours(1), stored.RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_token_is_a_no_op()
    {
        var account = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(account.Id);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await _service.RevokeAsync(account.Id, issued.Id));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await _service.RevokeAsync(account.Id, issued.Id));

        var stored = await _db.GitTokens.AsNoTracking().SingleAsync(t => t.Id == issued.Id);
        Assert.Equal(IssuedAt.AddHours(1), stored.RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_unknown_token_reports_no_match()
    {
        var account = await AddAccountAsync("alice");

        Assert.False(await _service.RevokeAsync(account.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_token_cannot_be_revoked_by_another_account()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        var aliceToken = await _service.IssueAsync(alice.Id);

        Assert.False(await _service.RevokeAsync(bob.Id, aliceToken.Id));
        Assert.NotNull(await _service.VerifyAsync("alice", aliceToken.Token));
    }

    [Fact]
    public async Task Login_password_is_not_accepted_as_a_git_credential()
    {
        const string LoginPassword = "correct horse battery staple";
        var hasher = new Argon2idPasswordHasher();

        var account = await AddAccountAsync("alice", hasher.Hash(LoginPassword));
        await _service.IssueAsync(account.Id);

        // Pinned against the real login path, not just assumed: this is the exact password that
        // succeeds through LoginService for this account.
        var loginService = new LoginService(_db, hasher, NullLogger<LoginService>.Instance);
        Assert.NotNull(await loginService.VerifyCredentialsAsync("alice", LoginPassword));

        // That same real login password, presented as the git token, does not resolve — there is
        // no password path into VerifyAsync, only a token path a password cannot enter.
        Assert.Null(await _service.VerifyAsync("alice", LoginPassword));
        Assert.Null(await _service.VerifyAsync("alice", account.PasswordHash));
    }

    [Fact]
    public async Task Tokens_are_listed_newest_first_including_revoked_ones()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");

        var oldest = await _service.IssueAsync(alice.Id);
        _time.Advance(TimeSpan.FromMinutes(1));
        var newest = await _service.IssueAsync(alice.Id);
        await _service.IssueAsync(bob.Id);

        await _service.RevokeAsync(alice.Id, oldest.Id);

        var listed = await _service.ListAsync(alice.Id);

        Assert.Equal(new[] { newest.Id, oldest.Id }, listed.Select(t => t.Id).ToArray());
        Assert.Null(listed[0].RevokedAt);
        Assert.NotNull(listed[1].RevokedAt);
    }

    [Fact]
    public async Task Listed_tokens_do_not_carry_the_at_rest_hash()
    {
        var account = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(account.Id);
        var storedHash = _tokenGenerator.ComputeHash(issued.Token);

        var listed = await _service.ListAsync(account.Id);

        // A record's ToString prints every property it has, so this fails the moment the
        // summary starts carrying the hash — which must never reach a render path.
        Assert.DoesNotContain(storedHash, listed.Single().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Token, listed.Single().ToString(), StringComparison.Ordinal);
    }

    private async Task<Account> AddAccountAsync(string username, string passwordHash = "$argon2id$stub")
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHash,
            DisplayName = username,
            CreatedAt = _time.GetUtcNow(),
        };

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return account;
    }

    private async Task<string> DumpGitTokenRowsAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT Id || '|' || TokenHash || '|' || CreatedAt || '|' || COALESCE(RevokedAt, '') FROM GitTokens";

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join('\n', rows);
    }
}
