using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;

namespace ZeroWiki.Tests.Data;

/// <summary>
/// Exercises the identity schema through the real EF Core migration (not
/// <c>EnsureCreated</c>) against an in-memory SQLite connection, so the generated
/// migration itself is under test.
/// </summary>
public sealed class IdentityDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;

    public IdentityDbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new IdentityDbContext(options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Account_with_git_emails_tokens_and_invitation_round_trips()
    {
        var admin = new Account
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = "argon2id$fake-hash",
            DisplayName = "Admin",
            IsAdministrator = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        admin.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = admin.Id, Email = "admin@example.com" });
        admin.GitTokens.Add(new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = admin.Id,
            TokenHash = "sha256-hash-of-token",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        _db.Accounts.Add(admin);
        await _db.SaveChangesAsync();

        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            TokenHash = "sha256-hash-of-invite",
            IssuerAccountId = admin.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync();

        await using var verify = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);

        var loaded = await verify.Accounts
            .Include(a => a.GitEmails)
            .Include(a => a.GitTokens)
            .SingleAsync(a => a.Id == admin.Id);

        Assert.Equal("admin", loaded.Username);
        Assert.True(loaded.IsAdministrator);
        Assert.Single(loaded.GitEmails);
        Assert.Equal("admin@example.com", loaded.GitEmails.Single().Email);
        Assert.Single(loaded.GitTokens);
        Assert.Null(loaded.GitTokens.Single().RevokedAt);

        var loadedInvitation = await verify.Invitations.SingleAsync(i => i.Id == invitation.Id);
        Assert.Equal(admin.Id, loadedInvitation.IssuerAccountId);
        Assert.Null(loadedInvitation.RedeemedAt);
        Assert.Null(loadedInvitation.RevokedAt);
    }

    [Fact]
    public async Task Duplicate_username_is_rejected()
    {
        _db.Accounts.Add(NewAccount("alice"));
        await _db.SaveChangesAsync();

        _db.Accounts.Add(NewAccount("alice"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_username_is_rejected_case_insensitively()
    {
        _db.Accounts.Add(NewAccount("alice"));
        await _db.SaveChangesAsync();

        _db.Accounts.Add(NewAccount("ALICE"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_git_email_is_rejected()
    {
        var first = NewAccount("alice");
        var second = NewAccount("bob");
        _db.Accounts.AddRange(first, second);
        await _db.SaveChangesAsync();

        _db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = first.Id, Email = "shared@example.com" });
        await _db.SaveChangesAsync();

        _db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = second.Id, Email = "shared@example.com" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_git_email_is_rejected_case_insensitively()
    {
        var first = NewAccount("alice");
        var second = NewAccount("bob");
        _db.Accounts.AddRange(first, second);
        await _db.SaveChangesAsync();

        _db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = first.Id, Email = "Alice@x.com" });
        await _db.SaveChangesAsync();

        _db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = second.Id, Email = "alice@x.com" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_git_token_hash_is_rejected()
    {
        var account = NewAccount("alice");
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        _db.GitTokens.Add(new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = "same-hash",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        _db.GitTokens.Add(new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = "same-hash",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_invitation_token_hash_is_rejected()
    {
        var issuer = NewAccount("alice");
        _db.Accounts.Add(issuer);
        await _db.SaveChangesAsync();

        _db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            TokenHash = "same-invite-hash",
            IssuerAccountId = issuer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await _db.SaveChangesAsync();

        _db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            TokenHash = "same-invite-hash",
            IssuerAccountId = issuer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Account_deletion_cascades_to_git_emails_and_tokens()
    {
        var account = NewAccount("alice");
        account.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = account.Id, Email = "alice@example.com" });
        account.GitTokens.Add(new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        _db.Accounts.Remove(account);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.GitEmails.ToListAsync());
        Assert.Empty(await _db.GitTokens.ToListAsync());
    }

    private static Account NewAccount(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "argon2id$fake-hash",
        DisplayName = username,
        IsAdministrator = false,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
