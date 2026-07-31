using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Exercises <see cref="InvitationService"/> against the real EF Core migration on an in-memory
/// SQLite connection, so issuing, scoping and revocation are tested through the actual schema.
/// </summary>
public sealed class InvitationServiceTests : IDisposable
{
    private const bool AsAdministrator = true;
    private const bool AsMember = false;

    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly FakeTimeProvider _time = new(IssuedAt);
    private readonly SecretTokenGenerator _tokenGenerator = new();
    private readonly InvitationService _service;

    public InvitationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();

        _service = new InvitationService(
            _db,
            _tokenGenerator,
            new CountingPasswordHasher(),
            _time,
            new CapturingLoggerProvider().CreateLogger<InvitationService>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Issued_invitation_is_stored_only_as_a_hash()
    {
        var alice = await AddAccountAsync("alice");

        var issued = await _service.IssueAsync(alice.Id);

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);

        Assert.Equal(_tokenGenerator.ComputeHash(issued.Token), stored.TokenHash);
        Assert.NotEqual(issued.Token, stored.TokenHash);
        Assert.Equal(alice.Id, stored.IssuerAccountId);
        Assert.Null(stored.RedeemedAt);
        Assert.Null(stored.RevokedAt);

        // The plaintext must appear nowhere in the persisted row, not merely in a different column.
        Assert.DoesNotContain(issued.Token, await DumpInvitationRowsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_invitations_never_share_a_token()
    {
        var alice = await AddAccountAsync("alice");

        var first = await _service.IssueAsync(alice.Id);
        var second = await _service.IssueAsync(alice.Id);

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task An_invitation_expires_the_policy_lifetime_after_it_is_issued()
    {
        var alice = await AddAccountAsync("alice");

        var issued = await _service.IssueAsync(alice.Id);

        Assert.Equal(IssuedAt, issued.CreatedAt);
        Assert.Equal(IssuedAt + InvitationPolicy.Lifetime, issued.ExpiresAt);

        // Deliberately the literal 7 and not InvitationPolicy.Lifetime — do not "DRY" this line
        // into the constant above it. This is the only assertion anywhere that pins AD14's number
        // to a real clock rather than restating whatever the constant currently says; the line
        // above it, and the page test, would both follow the constant silently if it changed.
        Assert.Equal(IssuedAt.AddDays(7), issued.ExpiresAt);

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Equal(issued.ExpiresAt, stored.ExpiresAt);
    }

    [Fact]
    public async Task The_expiry_is_fixed_at_issue_and_does_not_move_with_the_clock()
    {
        // The point of persisting ExpiresAt rather than re-deriving it: an invitation handed out
        // last Tuesday keeps last Tuesday's deadline no matter what happens afterwards.
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);

        _time.Advance(TimeSpan.FromDays(3));

        var listed = Assert.Single(await _service.ListAsync(alice.Id, AsMember));
        Assert.Equal(IssuedAt + InvitationPolicy.Lifetime, listed.ExpiresAt);
        Assert.Equal(IssuedAt, listed.CreatedAt);
    }

    [Fact]
    public async Task A_member_sees_only_their_own_invitations()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");

        var aliceInvitation = await _service.IssueAsync(alice.Id);
        await _service.IssueAsync(bob.Id);

        var listed = await _service.ListAsync(alice.Id, AsMember);

        Assert.Equal(new[] { aliceInvitation.Id }, listed.Select(i => i.Id).ToArray());
        Assert.Equal("alice", listed[0].IssuerUsername);
    }

    [Fact]
    public async Task An_administrator_sees_every_members_invitations()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        var root = await AddAccountAsync("root", isAdministrator: true);

        var aliceInvitation = await _service.IssueAsync(alice.Id);
        _time.Advance(TimeSpan.FromMinutes(1));
        var bobInvitation = await _service.IssueAsync(bob.Id);

        var listed = await _service.ListAsync(root.Id, AsAdministrator);

        // Newest first.
        Assert.Equal(new[] { bobInvitation.Id, aliceInvitation.Id }, listed.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "bob", "alice" }, listed.Select(i => i.IssuerUsername).ToArray());
    }

    [Fact]
    public async Task Listed_invitations_do_not_carry_the_at_rest_hash()
    {
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);

        var listed = Assert.Single(await _service.ListAsync(alice.Id, AsMember));

        // A record's ToString prints every property it has, so this fails the moment the summary
        // starts carrying the hash — which must never reach a render path.
        Assert.DoesNotContain(
            _tokenGenerator.ComputeHash(issued.Token),
            listed.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Token, listed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unused_invitation_is_revoked_by_its_issuer()
    {
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(
            InvitationRevocation.Revoked,
            await _service.RevokeAsync(alice.Id, AsMember, issued.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Equal(IssuedAt.AddHours(1), stored.RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_invitation_keeps_the_original_time()
    {
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(
            InvitationRevocation.Revoked,
            await _service.RevokeAsync(alice.Id, AsMember, issued.Id));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(
            InvitationRevocation.Revoked,
            await _service.RevokeAsync(alice.Id, AsMember, issued.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Equal(IssuedAt.AddHours(1), stored.RevokedAt);
    }

    [Fact]
    public async Task A_redeemed_invitation_cannot_be_revoked()
    {
        // The spec allows revocation "before redemption": the account this invitation created
        // still exists, so reporting success would claim to have undone something it did not.
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);
        await MarkRedeemedAsync(issued.Id);

        Assert.Equal(
            InvitationRevocation.AlreadyRedeemed,
            await _service.RevokeAsync(alice.Id, AsMember, issued.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Null(stored.RevokedAt);
        Assert.NotNull(stored.RedeemedAt);
    }

    [Fact]
    public async Task A_redeemed_invitation_is_not_revocable_by_an_administrator_either()
    {
        var alice = await AddAccountAsync("alice");
        var root = await AddAccountAsync("root", isAdministrator: true);
        var issued = await _service.IssueAsync(alice.Id);
        await MarkRedeemedAsync(issued.Id);

        Assert.Equal(
            InvitationRevocation.AlreadyRedeemed,
            await _service.RevokeAsync(root.Id, AsAdministrator, issued.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_unknown_invitation_reports_no_match()
    {
        var alice = await AddAccountAsync("alice");

        Assert.Equal(
            InvitationRevocation.NotFound,
            await _service.RevokeAsync(alice.Id, AsMember, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_member_cannot_revoke_another_members_invitation()
    {
        var alice = await AddAccountAsync("alice");
        var bob = await AddAccountAsync("bob");
        var aliceInvitation = await _service.IssueAsync(alice.Id);

        // Indistinguishable from an identifier that does not exist at all, so this cannot be used
        // to discover that someone else's invitation is there.
        Assert.Equal(
            InvitationRevocation.NotFound,
            await _service.RevokeAsync(bob.Id, AsMember, aliceInvitation.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == aliceInvitation.Id);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task An_administrator_can_revoke_another_members_invitation()
    {
        var alice = await AddAccountAsync("alice");
        var root = await AddAccountAsync("root", isAdministrator: true);
        var aliceInvitation = await _service.IssueAsync(alice.Id);

        _time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(
            InvitationRevocation.Revoked,
            await _service.RevokeAsync(root.Id, AsAdministrator, aliceInvitation.Id));

        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == aliceInvitation.Id);
        Assert.Equal(IssuedAt.AddHours(2), stored.RevokedAt);
    }

    [Fact]
    public async Task Revoking_under_an_already_cancelled_token_throws()
    {
        // Deliberately the opposite of D1's guarantee that revocation survives a disconnect: that
        // guarantee is a property of the caller, which passes CancellationToken.None (§3), not of
        // this method, which correctly honours whatever token it is given. This proves the
        // parameter is live — 4.5's sweep is what proves every caller passes None to it.
        var alice = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(alice.Id);

        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RevokeAsync(alice.Id, AsMember, issued.Id, cancellationToken));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        // The invitation stays exactly as issued: the throw happened before anything wrote.
        var stored = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Null(stored.RevokedAt);
    }

    private async Task<Account> AddAccountAsync(string username, bool isAdministrator = false)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "$argon2id$stub",
            DisplayName = username,
            IsAdministrator = isAdministrator,
            CreatedAt = _time.GetUtcNow(),
        };

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return account;
    }

    /// <summary>
    /// Stands in for Block 4b's redemption, which does not exist yet. Written through the store
    /// rather than the service so this block's tests do not depend on a flow it does not own.
    /// </summary>
    private async Task MarkRedeemedAsync(Guid invitationId)
    {
        var invitation = await _db.Invitations.SingleAsync(i => i.Id == invitationId);
        invitation.RedeemedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<string> DumpInvitationRowsAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT Id || '|' || TokenHash || '|' || IssuerAccountId || '|' || CreatedAt || '|' || ExpiresAt "
            + "|| '|' || COALESCE(RedeemedAt, '') || '|' || COALESCE(RevokedAt, '') FROM Invitations";

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join('\n', rows);
    }
}
