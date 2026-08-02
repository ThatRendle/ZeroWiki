using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Exercises invitation redemption — the anonymous half of §4 — against the real EF Core migration
/// on in-memory SQLite.
/// </summary>
/// <remarks>
/// The caller here has no account, no session and no audit trail, so these tests are as much about
/// what redemption <em>refuses to do</em> as about what it does: what it will not tell an anonymous
/// caller (AD17), what work it will not perform for one (BL1), and what it will not create.
/// </remarks>
public sealed class InvitationRedemptionTests : IDisposable
{
    private const string Password = "a good long passphrase";
    private const bool AsMember = false;

    private static readonly DateTimeOffset IssuedAt = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly IdentityDbContext _db;
    private readonly FakeTimeProvider _time = new(IssuedAt);
    private readonly SecretTokenGenerator _tokenGenerator = new();
    private readonly CountingPasswordHasher _passwordHasher = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<string> _executedSql = [];
    private readonly InvitationService _service;

    public InvitationRedemptionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>()
                .UseSqlite(_connection)
                .LogTo(_executedSql.Add, LogLevel.Information)
                .Options);
        _db.Database.Migrate();

        _service = new InvitationService(
            _db,
            _tokenGenerator,
            _passwordHasher,
            _time,
            _logs.CreateLogger<InvitationService>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Redeeming_a_valid_invitation_creates_the_account_and_consumes_the_invitation()
    {
        var issued = await IssueAsync();
        _time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));

        var account = Assert.Single(await _db.Accounts.AsNoTracking().Where(a => a.Username == "bob").ToListAsync());
        Assert.Equal("bob", account.DisplayName);
        Assert.Equal(IssuedAt.AddHours(3), account.CreatedAt);
        Assert.True(_passwordHasher.Verify(Password, account.PasswordHash));
        Assert.DoesNotContain(Password, account.PasswordHash, StringComparison.Ordinal);

        var invitation = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Equal(IssuedAt.AddHours(3), invitation.RedeemedAt);
        Assert.Null(invitation.RevokedAt);
    }

    [Fact]
    public async Task A_cancelled_redemption_leaves_the_invitation_still_redeemable()
    {
        // Pre-cancelled, not mid-flight. The first cancellable await here is RejectionAsync's
        // lookup (:260), before password hashing or the write lock, so it throws before
        // redemption begins at all. That proves the method is cancellable and that an early
        // cancel leaves nothing behind, and no more than that: it never enters the write lock or
        // the SaveChangesAsync/CommitAsync window, so it is the *weakest* form of this claim and
        // cannot fail if the transactional rollback there were broken. See the mid-flight test
        // below for the property this one cannot reach.
        //
        // "Still redeemable" is not tested here as "not redeemed" — it is proved usable: a
        // second, live redemption of the same token has to succeed afterwards.
        var issued = await IssueAsync();
        var cancellationToken = new CancellationToken(canceled: true);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.RedeemAsync(issued.Token, "bob", Password, cancellationToken));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        // Against the store, not the return value: no account exists, and the invitation carries
        // neither a redemption nor a revocation.
        await AssertNoAccountBeyondTheIssuerAsync();
        var afterCancellation = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Null(afterCancellation.RedeemedAt);
        Assert.Null(afterCancellation.RevokedAt);

        // Usable, not merely untouched: the same token still redeems successfully.
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));

        var account = Assert.Single(await _db.Accounts.AsNoTracking().Where(a => a.Username == "bob").ToListAsync());
        Assert.NotNull(account);
        Assert.NotNull((await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id)).RedeemedAt);
    }

    [Fact]
    public async Task A_cancellation_between_the_write_and_the_commit_still_rolls_back_and_stays_redeemable()
    {
        // Reaches the window the pre-cancelled test above cannot: cancel the token only once
        // SaveChangesAsync has finished writing the new account and the consumed invitation into
        // the still-uncommitted transaction, so the token stays live through the whole
        // check-then-act and only goes cancelled right before CommitAsync (:318-319) runs. If
        // that rollback were broken — if the account or the redemption survived a cancelled
        // commit — this test, unlike the pre-cancelled one, would see it and fail.
        var issued = await IssueAsync();
        var cancellationTokenSource = new CancellationTokenSource();

        await using var interceptingDb = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new CancelAfterSaveInterceptor(cancellationTokenSource))
                .Options);
        var interceptingService = new InvitationService(
            interceptingDb,
            _tokenGenerator,
            _passwordHasher,
            _time,
            _logs.CreateLogger<InvitationService>());

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interceptingService.RedeemAsync(issued.Token, "bob", Password, cancellationTokenSource.Token));

        Assert.True(thrown.CancellationToken.IsCancellationRequested);

        // Against the store: no account, and the invitation carries neither timestamp.
        await AssertNoAccountBeyondTheIssuerAsync();
        var afterCancellation = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.Null(afterCancellation.RedeemedAt);
        Assert.Null(afterCancellation.RevokedAt);

        // Usable, not merely rolled back: the same token still redeems successfully afterwards.
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));
        Assert.NotNull((await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id)).RedeemedAt);
    }

    [Fact]
    public async Task An_invitation_never_creates_an_administrator()
    {
        // The only route to IsAdministrator is the one-time bootstrap. An invitation grants
        // membership; if this ever changed, every member could mint an administrator.
        var issued = await IssueAsync();

        await _service.RedeemAsync(issued.Token, "bob", Password);

        Assert.False((await _db.Accounts.AsNoTracking().SingleAsync(a => a.Username == "bob")).IsAdministrator);
    }

    [Fact]
    public async Task The_chosen_username_is_trimmed_before_it_is_stored()
    {
        var issued = await IssueAsync();

        Assert.Equal(
            InvitationRedemption.Redeemed,
            await _service.RedeemAsync(issued.Token, "  bob\n", Password));

        Assert.Equal("bob", (await _db.Accounts.AsNoTracking().SingleAsync(a => a.Username != "alice")).Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    [InlineData("Kf9nq3xJj5vQ2mZ8rT1yP0wL4hB7dS6cN9aE3gU5iO0")]
    [InlineData("../../etc/passwd")]
    public async Task A_token_matching_nothing_gets_one_uniform_refusal_and_creates_no_account(string token)
    {
        // AD17's other half: every input an anonymous caller can supply without possessing a real
        // token produces the same answer, so nothing here distinguishes "wrong" from "malformed"
        // from "there is no such invitation".
        await IssueAsync();

        Assert.Equal(InvitationRedemption.NotValid, await _service.RedeemAsync(token, "bob", Password));
        Assert.Equal(InvitationRedemption.NotValid, await _service.ValidateAsync(token));

        Assert.Empty(await _db.Accounts.AsNoTracking().Where(a => a.Username == "bob").ToListAsync());
    }

    [Theory]
    [InlineData("not-a-real-token")]
    [InlineData("")]
    public async Task An_unmatched_token_naming_an_existing_username_still_gets_the_uniform_refusal(string token)
    {
        // AD17's boundary for the one outcome that was kept *because* it sits inside it. Every other
        // garbage-token test here names a username that does not exist, so none of them can see a
        // uniqueness check that had drifted in front of the token lookup — and that drift turns
        // UsernameTaken into username enumeration by an anonymous stranger holding no invitation at
        // all, which is the exact oracle the outcome's <remarks> argues is acceptable *only* because
        // the prober must hold a live invitation. "alice" is the seeded issuer, so it exists.
        await IssueAsync();

        Assert.Equal(
            InvitationRedemption.NotValid,
            await _service.RedeemAsync(token, "alice", Password));

        // And the answer is the same one an unknown username gets, so the pair cannot be diffed.
        Assert.Equal(
            await _service.RedeemAsync(token, "nobody", Password),
            await _service.RedeemAsync(token, "alice", Password));
    }

    [Fact]
    public async Task An_invitation_that_expires_while_the_caller_waits_for_the_lock_is_refused()
    {
        // The under-lock check is the one that binds, so it has to compare against the moment it
        // runs — not against a clock read before ~93 ms of Argon2id and an unbounded wait on
        // BEGIN IMMEDIATE. This provider is that gap: the pre-lock read sees a live invitation, and
        // every read after it sees one that has expired.
        var issued = await IssueAsync();
        var expiredService = new InvitationService(
            _db,
            _tokenGenerator,
            _passwordHasher,
            new SteppingTimeProvider(IssuedAt, IssuedAt + InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1)),
            _logs.CreateLogger<InvitationService>());

        Assert.Equal(
            InvitationRedemption.Expired,
            await expiredService.RedeemAsync(issued.Token, "bob", Password));

        await AssertNoAccountBeyondTheIssuerAsync();
        Assert.Null((await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id)).RedeemedAt);
    }

    [Fact]
    public async Task An_expired_invitation_is_rejected_and_creates_no_account()
    {
        var issued = await IssueAsync();
        _time.Advance(InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(InvitationRedemption.Expired, await _service.RedeemAsync(issued.Token, "bob", Password));
        await AssertNoAccountBeyondTheIssuerAsync();
    }

    [Fact]
    public async Task An_invitation_is_still_redeemable_in_its_final_moment()
    {
        // The fail-open mirror of the test above, and the reason the comparison is strict: an
        // invitation is dead the instant it expires, not a moment before.
        var issued = await IssueAsync();
        _time.Advance(InvitationPolicy.Lifetime - TimeSpan.FromSeconds(1));

        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));
    }

    [Fact]
    public async Task An_already_redeemed_invitation_creates_no_second_account()
    {
        var issued = await IssueAsync();
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));

        Assert.Equal(
            InvitationRedemption.AlreadyRedeemed,
            await _service.RedeemAsync(issued.Token, "carol", Password));

        Assert.Empty(await _db.Accounts.AsNoTracking().Where(a => a.Username == "carol").ToListAsync());
        Assert.Equal(2, await _db.Accounts.CountAsync());
    }

    [Fact]
    public async Task A_revoked_invitation_cannot_be_redeemed_and_creates_no_account()
    {
        var issuer = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(issuer.Id);
        Assert.Equal(InvitationRevocation.Revoked, await _service.RevokeAsync(issuer.Id, AsMember, issued.Id));

        Assert.Equal(InvitationRedemption.Revoked, await _service.RedeemAsync(issued.Token, "bob", Password));
        await AssertNoAccountBeyondTheIssuerAsync();
    }

    [Fact]
    public async Task Revoking_after_a_real_redemption_reports_that_it_was_already_redeemed()
    {
        // 4a could only prove this enum value by writing RedeemedAt through the store directly.
        // This is the end-to-end case it could not reach.
        var issuer = await AddAccountAsync("alice");
        var issued = await _service.IssueAsync(issuer.Id);

        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));

        Assert.Equal(
            InvitationRevocation.AlreadyRedeemed,
            await _service.RevokeAsync(issuer.Id, AsMember, issued.Id));

        var invitation = await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id);
        Assert.NotNull(invitation.RedeemedAt);
        Assert.Null(invitation.RevokedAt);
    }

    [Fact]
    public async Task The_three_reasons_a_token_holder_is_told_are_distinct_from_each_other()
    {
        // AD17: someone holding a real token has already proved possession of a 256-bit secret, so
        // naming the reason enumerates nothing — and a genuine invitee whose link expired has to be
        // able to tell that from a typo, or they retry instead of asking for a new link.
        var issuer = await AddAccountAsync("alice");

        var expired = await _service.IssueAsync(issuer.Id);
        var revoked = await _service.IssueAsync(issuer.Id);
        var used = await _service.IssueAsync(issuer.Id);

        await _service.RevokeAsync(issuer.Id, AsMember, revoked.Id);
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(used.Token, "bob", Password));

        _time.Advance(InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1));

        InvitationRedemption?[] reasons =
        [
            await _service.ValidateAsync(expired.Token),
            await _service.ValidateAsync(revoked.Token),
            await _service.ValidateAsync(used.Token),
            await _service.ValidateAsync("not-a-real-token"),
        ];

        Assert.Equal(
            [
                InvitationRedemption.Expired,
                InvitationRedemption.Revoked,
                InvitationRedemption.AlreadyRedeemed,
                InvitationRedemption.NotValid,
            ],
            reasons);
    }

    [Fact]
    public async Task A_used_invitation_reads_as_used_even_once_it_has_also_expired()
    {
        // Precedence, pinned: the issuer's list calls this row "Used", and the invitee must not be
        // told "expired" about an invitation that did create an account.
        var issued = await IssueAsync();
        await _service.RedeemAsync(issued.Token, "bob", Password);

        _time.Advance(InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(InvitationRedemption.AlreadyRedeemed, await _service.ValidateAsync(issued.Token));
    }

    [Fact]
    public async Task No_password_is_hashed_for_a_token_that_was_never_going_to_work()
    {
        // BL1's lesson on the surface that most deserves it. Redemption is anonymous, so an
        // attacker who can make the server derive a 64 MiB Argon2id hash by posting a garbage token
        // has a free amplifier — the cheap token lookup has to come first.
        var issuer = await AddAccountAsync("alice");

        var expired = await _service.IssueAsync(issuer.Id);
        var revoked = await _service.IssueAsync(issuer.Id);
        var used = await _service.IssueAsync(issuer.Id);
        await _service.RevokeAsync(issuer.Id, AsMember, revoked.Id);
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(used.Token, "bob", Password));

        _time.Advance(InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1));
        _passwordHasher.Forget();

        Assert.Equal(InvitationRedemption.NotValid, await _service.RedeemAsync("garbage", "carol", Password));
        Assert.Equal(InvitationRedemption.Expired, await _service.RedeemAsync(expired.Token, "carol", Password));
        Assert.Equal(InvitationRedemption.Revoked, await _service.RedeemAsync(revoked.Token, "carol", Password));
        Assert.Equal(InvitationRedemption.AlreadyRedeemed, await _service.RedeemAsync(used.Token, "carol", Password));

        Assert.Empty(_passwordHasher.Derivations);
    }

    [Fact]
    public async Task The_password_is_hashed_once_when_the_invitation_is_good()
    {
        // The complement of the test above: "no hashing" must not be achievable by never hashing.
        var issued = await IssueAsync();

        await _service.RedeemAsync(issued.Token, "bob", Password);

        Assert.Equal([Password], _passwordHasher.Derivations);
    }

    [Fact]
    public async Task A_taken_username_refuses_without_consuming_the_invitation()
    {
        var issued = await IssueAsync();

        Assert.Equal(
            InvitationRedemption.UsernameTaken,
            await _service.RedeemAsync(issued.Token, "alice", Password));

        Assert.Null((await _db.Invitations.AsNoTracking().SingleAsync(i => i.Id == issued.Id)).RedeemedAt);

        // Still good, which is the point of not consuming it.
        Assert.Equal(InvitationRedemption.Redeemed, await _service.RedeemAsync(issued.Token, "bob", Password));
    }

    [Fact]
    public async Task Usernames_that_differ_only_in_case_are_the_same_username()
    {
        // The column collates NOCASE, so the service has to refuse here rather than let the unique
        // index throw — and "Alice" must not become a second account beside "alice".
        var issued = await IssueAsync();

        Assert.Equal(
            InvitationRedemption.UsernameTaken,
            await _service.RedeemAsync(issued.Token, "ALICE", Password));

        Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_account_table_itself_refuses_a_duplicate_username_independent_of_redemptions_own_check()
    {
        // Every UsernameTaken assertion above goes through RedeemAsync's own pre-insert AnyAsync
        // check (InvitationService.cs:286), which would refuse a clash whether or not
        // AccountConfiguration's unique index on Username exists — so none of them would notice the
        // index being dropped. This bypasses that check and writes straight to the store, which is
        // what IdentityDbContextTests.Duplicate_username_is_rejected proves in isolation; the
        // assertion belongs here too because nothing otherwise connects that proof to the schema
        // this test class exercises the service against (supervisor S2, §7b, extended to accounts).
        await AddAccountAsync("alice");

        _db.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            PasswordHash = "$argon2id$stub",
            DisplayName = "alice",
            CreatedAt = _time.GetUtcNow(),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        Assert.Equal("alice", Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("___")]
    [InlineData("café")]
    public async Task A_username_outside_the_permitted_charset_is_refused_at_the_boundary(string username)
    {
        // AD11, from the same constant as bootstrap — the git remote presents the username as the
        // Basic-auth userid, where a colon is structurally illegal.
        var issued = await IssueAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RedeemAsync(issued.Token, username, Password));

        Assert.Contains(CredentialPolicy.UsernameRuleDescription, error.Message, StringComparison.Ordinal);
        Assert.Empty(_passwordHasher.Derivations);
        await AssertNoAccountBeyondTheIssuerAsync();
    }

    [Fact]
    public async Task A_password_below_the_minimum_length_is_refused_at_the_boundary()
    {
        // AD10, from the same constant as bootstrap, so the only two paths where somebody chooses
        // their own password cannot diverge.
        var issued = await IssueAsync();
        var tooShort = new string('x', CredentialPolicy.MinimumPasswordLength - 1);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RedeemAsync(issued.Token, "bob", tooShort));

        Assert.Contains(
            CredentialPolicy.MinimumPasswordLengthRuleDescription,
            error.Message,
            StringComparison.Ordinal);
        Assert.Empty(_passwordHasher.Derivations);
        await AssertNoAccountBeyondTheIssuerAsync();
    }

    [Fact]
    public void The_redeemability_predicate_is_evaluated_in_sql()
    {
        // AD7, and the single most important assertion in §4. Expiry is a security boundary, and
        // the built-in DateTimeOffsetToBinaryConverter was measured silently admitting an expired
        // row — a predicate that ran on the client would compare whatever a converter handed back
        // and would fail open on exactly that bug. ToQueryString only succeeds for a fully
        // translated query, so this cannot pass on a lucky client-side filter.
        var sql = InvitationService.Redeemable(_db.Invitations.AsNoTracking(), IssuedAt).ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("\"ExpiresAt\" > ", sql, StringComparison.Ordinal);
        Assert.Contains("\"RedeemedAt\" IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"RevokedAt\" IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_expiry_comparison_reaches_sqlite_on_the_redemption_path_itself()
    {
        // The predicate above is only worth anything if redemption is what runs it. This reads the
        // statements SQLite actually executed, so it cannot be satisfied by a helper nobody calls.
        var issued = await IssueAsync();
        _executedSql.Clear();

        await _service.RedeemAsync(issued.Token, "bob", Password);

        Assert.Contains(
            _executedSql,
            statement => statement.Contains("\"ExpiresAt\" > ", StringComparison.Ordinal)
                && statement.Contains("\"RedeemedAt\" IS NULL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_row_is_excluded_by_the_shared_predicate_in_the_store()
    {
        var issued = await IssueAsync();
        _time.Advance(InvitationPolicy.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Empty(
            await InvitationService.Redeemable(_db.Invitations.AsNoTracking(), _time.GetUtcNow())
                .Where(i => i.Id == issued.Id)
                .ToListAsync());
    }

    [Fact]
    public async Task A_successful_redemption_records_which_account_the_invitation_produced()
    {
        // The store cannot answer this: Invitation carries RedeemedAt but no RedeemedByAccountId,
        // so once a row is consumed it says that it was used and not by whom. In an invite-only
        // system "who invited whom" is the audit question that eventually gets asked, and this log
        // line is the only place the answer exists.
        var issued = await IssueAsync();

        await _service.RedeemAsync(issued.Token, "bob", Password);

        var accountId = (await _db.Accounts.AsNoTracking().SingleAsync(a => a.Username == "bob")).Id;
        var line = Assert.Single(_logs.Messages, message => message.Contains("redeemed", StringComparison.Ordinal));

        Assert.Contains(issued.Id.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(accountId.ToString(), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_refusal_names_its_reason_in_the_log()
    {
        var issuer = await AddAccountAsync("alice");
        var revoked = await _service.IssueAsync(issuer.Id);
        await _service.RevokeAsync(issuer.Id, AsMember, revoked.Id);
        var live = await _service.IssueAsync(issuer.Id);

        await _service.RedeemAsync("garbage", "bob", Password);
        await _service.RedeemAsync(revoked.Token, "bob", Password);
        await _service.RedeemAsync(live.Token, "alice", Password);

        Assert.Equal(
            [
                $"Invitation redemption refused: {InvitationRedemption.NotValid}.",
                $"Invitation redemption refused: {InvitationRedemption.Revoked}.",
                $"Invitation redemption refused: {InvitationRedemption.UsernameTaken}.",
            ],
            _logs.Messages.Where(m => m.StartsWith("Invitation redemption refused", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task No_token_hash_or_password_ever_reaches_the_log()
    {
        // The token is high-entropy and the log is not a place secrets live. Asserted over every
        // path, refusals included, because a rejection is the one most likely to be "helpfully"
        // annotated with the value that failed.
        var issued = await IssueAsync();

        await _service.RedeemAsync("garbage", "bob", Password);
        await _service.RedeemAsync(issued.Token, "alice", Password);
        await _service.RedeemAsync(issued.Token, "bob", Password);
        await _service.RedeemAsync(issued.Token, "carol", Password);

        // Written, not Messages. Measured rather than assumed: an argument beyond the template's
        // placeholders reaches no sink at all, and a value in a placeholder is in the message
        // anyway — but a value carried by a log *scope* reaches a structured sink while appearing
        // in no message, so a message-only sweep would wave exactly that shape through.
        var log = string.Join('\n', _logs.Written);

        Assert.DoesNotContain(issued.Token, log, StringComparison.Ordinal);
        Assert.DoesNotContain(_tokenGenerator.ComputeHash(issued.Token), log, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, log, StringComparison.Ordinal);
        Assert.DoesNotContain("garbage", log, StringComparison.Ordinal);
    }

    private async Task<IssuedInvitation> IssueAsync() =>
        await _service.IssueAsync((await AddAccountAsync("alice")).Id);

    private async Task AssertNoAccountBeyondTheIssuerAsync() =>
        Assert.Equal("alice", Assert.Single(await _db.Accounts.AsNoTracking().ToListAsync()).Username);

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
}
