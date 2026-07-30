using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Identity;

/// <summary>
/// Issues, lists, and revokes the single-use invitations that are the only way an account comes
/// into existence after the first administrator. Only token hashes are persisted.
/// </summary>
/// <remarks>
/// <para>
/// The issuing, listing and revoking methods take the calling account's identity and decide for
/// themselves what that caller may see and do (AD15). Scoping the query rather than filtering a
/// rendered list is the point: a filter in a view leaks through the next surface that reads the
/// same data, and a route that forgets to check cannot reach past this boundary.
/// </para>
/// <para>
/// <see cref="RedeemAsync"/> is the exception, and deliberately so: its caller has no account yet,
/// so the presented token <em>is</em> the authorisation. That makes it the one anonymously
/// reachable method here, and everything it does before the token has matched a stored hash is
/// attacker-reachable work.
/// </para>
/// </remarks>
public sealed class InvitationService(
    IdentityDbContext db,
    ISecretTokenGenerator tokenGenerator,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    ILogger<InvitationService> logger)
{
    /// <summary>
    /// Issues a new invitation on behalf of an account. The returned plaintext token is the only
    /// copy in existence; the store holds nothing but its hash.
    /// </summary>
    public async Task<IssuedInvitation> IssueAsync(
        Guid issuerAccountId,
        CancellationToken cancellationToken = default)
    {
        var secret = tokenGenerator.Generate();
        var createdAt = timeProvider.GetUtcNow();

        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            TokenHash = secret.Hash,
            IssuerAccountId = issuerAccountId,
            CreatedAt = createdAt,

            // Computed once, here, and persisted. An invitation's lifetime is a property of that
            // invitation, fixed when it was handed out — deriving it at redemption instead would
            // re-date every outstanding invitation the day InvitationPolicy.Lifetime changes.
            ExpiresAt = createdAt + InvitationPolicy.Lifetime,
        };

        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);

        return new IssuedInvitation(invitation.Id, secret.Plaintext, invitation.CreatedAt, invitation.ExpiresAt);
    }

    /// <summary>
    /// Lists the invitations the caller may see, newest first: their own, or every account's when
    /// the caller is an administrator (AD15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the join buys, precisely: the issuer's <see cref="Account"/> row is <em>never
    /// materialised</em>, only its username is selected, so an unreadable value-converted timestamp
    /// on an account row cannot reach this query at all. That is the §7 hazard — one corrupt
    /// account poisoning a list everybody reads — and it is designed out here rather than survived.
    /// </para>
    /// <para>
    /// It buys nothing for the invitation's own timestamps, and the comment this replaced was wrong
    /// to imply otherwise: <c>CreatedAt</c>, <c>ExpiresAt</c>, <c>RedeemedAt</c> and
    /// <c>RevokedAt</c> are all selected, so a corrupt one still throws and still takes the whole
    /// list down. Nothing short of not reading the column would prevent that, and the list exists
    /// to show those columns. Copy this shape for the account side; do not copy it expecting it to
    /// protect the columns you actually project.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<InvitationSummary>> ListAsync(
        Guid callerAccountId,
        bool callerIsAdministrator,
        CancellationToken cancellationToken = default)
    {
        var visible = db.Invitations.AsNoTracking();

        if (!callerIsAdministrator)
        {
            visible = visible.Where(i => i.IssuerAccountId == callerAccountId);
        }

        // Joined rather than navigated so the issuer's username can be read without a
        // null-forgiving operator over an optional navigation property.
        return await (from invitation in visible
                      join issuer in db.Accounts.AsNoTracking()
                        on invitation.IssuerAccountId equals issuer.Id
                      orderby invitation.CreatedAt descending
                      select new InvitationSummary(
                          invitation.Id,
                          invitation.IssuerAccountId,
                          issuer.Username,
                          invitation.CreatedAt,
                          invitation.ExpiresAt,
                          invitation.RedeemedAt,
                          invitation.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Revokes an invitation the caller may act on — their own, or anyone's when the caller is an
    /// administrator (AD15). Idempotent: re-revoking leaves the original revocation time in place.
    /// </summary>
    /// <remarks>
    /// The read and the write are one transaction, and the transaction takes the store's write lock
    /// up front. Without that this is check-then-act: a redemption committing between the
    /// <c>RedeemedAt is not null</c> test and the write would leave a row carrying <em>both</em>
    /// timestamps, and would tell the revoker <see cref="InvitationRevocation.Revoked"/> about an
    /// invitation that had already created an account — the exact confusion
    /// <see cref="InvitationRevocation.AlreadyRedeemed"/> exists to prevent.
    /// </remarks>
    public async Task<InvitationRevocation> RevokeAsync(
        Guid callerAccountId,
        bool callerIsAdministrator,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        await using var writeLock = await BeginWriteLockedTransactionAsync(cancellationToken);

        var scoped = db.Invitations.Where(i => i.Id == invitationId);

        if (!callerIsAdministrator)
        {
            scoped = scoped.Where(i => i.IssuerAccountId == callerAccountId);
        }

        var invitation = await scoped.SingleOrDefaultAsync(cancellationToken);

        if (invitation is null)
        {
            return InvitationRevocation.NotFound;
        }

        // The spec permits revocation "before redemption" only. The account this invitation
        // created still exists, so there is nothing here to withdraw.
        if (invitation.RedeemedAt is not null)
        {
            return InvitationRevocation.AlreadyRedeemed;
        }

        if (invitation.RevokedAt is null)
        {
            invitation.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await writeLock.CommitAsync(cancellationToken);
        }

        return InvitationRevocation.Revoked;
    }

    /// <summary>
    /// Why a presented token cannot be redeemed right now, or <see langword="null"/> when nothing
    /// stands in its way.
    /// </summary>
    /// <remarks>
    /// Advisory: it exists so the redemption page can say "this link has expired" instead of
    /// showing a form that is going to fail. <see cref="RedeemAsync"/> decides again under the
    /// write lock and is the only answer that binds.
    /// </remarks>
    public async Task<InvitationRedemption?> ValidateAsync(
        string? presentedToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return InvitationRedemption.NotValid;
        }

        return await RejectionAsync(
            tokenGenerator.ComputeHash(presentedToken),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    /// <summary>
    /// Redeems an invitation: creates the account the invitee chose credentials for and marks the
    /// invitation consumed, as one atomic step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Single-use" is a concurrency requirement, not merely a logical one — the same requirement
    /// "exactly one administrator" was, and it fails the same way. Checking that
    /// <c>RedeemedAt IS NULL</c> inside SQLite's default deferred transaction is not enough: the
    /// read takes no write lock, so two simultaneous redemptions of one invitation both observe it
    /// unredeemed and both inserts succeed. Taking the write lock up front (<c>BEGIN IMMEDIATE</c>)
    /// serialises them, so the second reads the first's committed row and refuses.
    /// </para>
    /// <para>
    /// The order of the steps is the security-relevant part, and it is pulled in two directions at
    /// once. The Argon2id hash costs ~93 ms at 64 MiB, so it must not happen <em>inside</em> the
    /// lock, where it would serialise every other writer behind a CPU burn. But this route is
    /// anonymous, so it must not happen <em>before</em> the cheap validity checks either: an
    /// attacker who can make the server derive a 64 MiB hash by posting a garbage token has a free
    /// amplifier. Hence: hash the token, check validity, hash the password, take the lock, re-check
    /// under it, insert.
    /// </para>
    /// </remarks>
    public async Task<InvitationRedemption> RedeemAsync(
        string? presentedToken,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var trimmedUsername = username.Trim();

        // The same two guards bootstrap applies, from the same constants. AD11's charset is a
        // structural invariant — the git remote presents the username as the Basic-auth userid,
        // where RFC 7617 makes a colon illegal — so it belongs at the boundary unconditionally.
        // AD10's minimum is here because AD10 itself names this path: "applies to every path where
        // a user chooses a password — §3 bootstrap and §4 invitation redemption — so the two cannot
        // diverge". That is the decision scoping itself to both, not §3's exception being widened.
        // Both sit in front of the password hash, so neither can become another way to spend 64 MiB
        // on a request that was always going to be refused.
        if (trimmedUsername.Length > CredentialPolicy.MaximumUsernameLength
            || !CredentialPolicy.UsernameMatcher().IsMatch(trimmedUsername))
        {
            throw new ArgumentException(CredentialPolicy.UsernameRuleDescription, nameof(username));
        }

        if (password.Length < CredentialPolicy.MinimumPasswordLength)
        {
            throw new ArgumentException(CredentialPolicy.MinimumPasswordLengthRuleDescription, nameof(password));
        }

        if (string.IsNullOrEmpty(presentedToken))
        {
            return Rejected(InvitationRedemption.NotValid);
        }

        var now = timeProvider.GetUtcNow();
        var tokenHash = tokenGenerator.ComputeHash(presentedToken);

        // Cheap and anonymous-facing: one indexed lookup on the token hash, before any key
        // derivation. Advisory only — the decision that counts is made under the lock below — but
        // it is what keeps an unknown token from costing the server a 64 MiB hash.
        if (await RejectionAsync(tokenHash, now, cancellationToken) is { } rejection)
        {
            return Rejected(rejection);
        }

        var passwordHash = passwordHasher.Hash(password);

        await using var writeLock = await BeginWriteLockedTransactionAsync(cancellationToken);

        // The clock is read again, and that is not fussiness. `now` above was captured before ~93 ms
        // of Argon2id and before an unbounded wait on BEGIN IMMEDIATE — SQLite's default busy
        // timeout is 30 s — so a decision made against it can admit an invitation that expired while
        // this caller was queued behind another writer. Expiry is a security boundary (AD7), and the
        // check that binds has to compare against the moment it actually runs.
        var underLock = timeProvider.GetUtcNow();

        // The authoritative decision, evaluated by SQLite inside the write lock. Expiry is compared
        // in SQL rather than in memory on purpose; see Redeemable.
        var invitation = await Redeemable(db.Invitations.Where(i => i.TokenHash == tokenHash), underLock)
            .SingleOrDefaultAsync(cancellationToken);

        if (invitation is null)
        {
            // The row was redeemed, revoked or expired between the check above and this lock. The
            // re-read only names what happened; if it cannot — which would mean SQL and this
            // process disagree about the same row, the AD7 failure mode — the answer stays a
            // refusal rather than becoming a redemption.
            return Rejected(
                await RejectionAsync(tokenHash, underLock, cancellationToken) ?? InvitationRedemption.NotValid);
        }

        // Under the lock, so it cannot be raced by a second redemption choosing the same name. The
        // column collates NOCASE, so this is the case-insensitive comparison the unique index will
        // apply, not a stricter one that would let the insert throw instead.
        if (await db.Accounts.AnyAsync(a => a.Username == trimmedUsername, cancellationToken))
        {
            return Rejected(InvitationRedemption.UsernameTaken);
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = trimmedUsername,
            PasswordHash = passwordHash,
            DisplayName = trimmedUsername,

            // An invitation grants membership and nothing more. Administrators exist only through
            // the bootstrap; there is no path here that could elevate one.
            IsAdministrator = false,
            CreatedAt = underLock,
        };

        db.Accounts.Add(account);
        invitation.RedeemedAt = underLock;

        // One SaveChanges, one commit: the account and the consumed invitation land together or
        // not at all, so there is no window in which an account exists against an invitation still
        // advertising itself as unused.
        await db.SaveChangesAsync(cancellationToken);
        await writeLock.CommitAsync(cancellationToken);

        // The only record anywhere of which account an invitation produced. Invitation carries
        // RedeemedAt but no RedeemedByAccountId, so once a row is consumed the store can say that
        // it was used and not by whom — and "who invited whom" is the audit question an invite-only
        // system eventually gets asked. Adding the column would be a schema change outside this
        // change's specs; this line closes the gap at no cost. Do not delete it as chatter.
        logger.LogInformation(
            "Invitation {InvitationId} redeemed: it created account {AccountId}.",
            invitation.Id,
            account.Id);

        return InvitationRedemption.Redeemed;
    }

    /// <summary>
    /// Records a refused redemption and returns the outcome unchanged.
    /// </summary>
    /// <remarks>
    /// The reason is recorded where an operator can see it, which is the same posture
    /// <see cref="LoginService"/> takes. Never the presented token, never its hash, never the
    /// password: none of the three helps answer "why can my invitee not get in", and all three
    /// would make the log a place secrets live.
    /// </remarks>
    private InvitationRedemption Rejected(InvitationRedemption outcome)
    {
        logger.LogInformation("Invitation redemption refused: {Outcome}.", outcome);

        return outcome;
    }

    /// <summary>
    /// The invitations a presented token may still be redeemed against.
    /// </summary>
    /// <remarks>
    /// Public, and shared with the test that pins AD7, so the predicate that decides redemption is
    /// the same expression the test reads the SQL of. Expiry is a security boundary: the built-in
    /// <c>DateTimeOffsetToBinaryConverter</c> was measured silently admitting an expired row, which
    /// is why the store holds fixed-width ISO-8601 UTC text instead. Comparing in SQL is what makes
    /// that representation the thing being trusted; a filter evaluated in memory would compare
    /// whatever a converter handed back and would fail open on exactly that bug.
    /// </remarks>
    public static IQueryable<Invitation> Redeemable(IQueryable<Invitation> invitations, DateTimeOffset asOf) =>
        invitations.Where(i => i.RedeemedAt == null && i.RevokedAt == null && i.ExpiresAt > asOf);

    /// <summary>
    /// Why the invitation with this token hash cannot be redeemed, or <see langword="null"/> when
    /// it can.
    /// </summary>
    /// <remarks>
    /// The precedence — used, then revoked, then expired — matches how the issuer's list describes
    /// the same row, so the two surfaces cannot tell different stories about one invitation.
    /// </remarks>
    private async Task<InvitationRedemption?> RejectionAsync(
        string tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await db.Invitations
            .AsNoTracking()
            .Where(i => i.TokenHash == tokenHash)
            .Select(i => new InvitationState(i.ExpiresAt, i.RedeemedAt, i.RevokedAt))
            .SingleOrDefaultAsync(cancellationToken);

        return state switch
        {
            null => InvitationRedemption.NotValid,
            { RedeemedAt: not null } => InvitationRedemption.AlreadyRedeemed,
            { RevokedAt: not null } => InvitationRedemption.Revoked,
            { ExpiresAt: var expiresAt } when expiresAt <= now => InvitationRedemption.Expired,
            _ => null,
        };
    }

    /// <summary>
    /// Opens a transaction holding the store's write lock from its first statement
    /// (<c>BEGIN IMMEDIATE</c>), enlisted on the context.
    /// </summary>
    /// <remarks>
    /// There is no async overload accepting <c>deferred</c>, and EF's own
    /// <c>BeginTransactionAsync()</c> gives a deferred transaction — precisely the one that does
    /// not hold the lock across a read.
    /// </remarks>
    private async Task<WriteLock> BeginWriteLockedTransactionAsync(CancellationToken cancellationToken)
    {
        // Opened through EF so its own open-count bookkeeping stays straight; the raw connection is
        // still needed because only the non-async overload accepts `deferred`.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();

        var transaction = connection.BeginTransaction(deferred: false);

        // Enlisted so EF's own bookkeeping stays straight: committing the raw SqliteTransaction
        // directly would leave the context still associated with it, and the next query on this
        // context would fail against a disposed transaction. Only ever null when null is passed in.
        var enlistment = await db.Database.UseTransactionAsync(transaction, cancellationToken)
            ?? throw new InvalidOperationException("Could not enlist the invitation write transaction.");

        return new WriteLock(transaction, enlistment);
    }

    /// <summary>
    /// The store's write lock, held for the life of the scope and released by disposal — rolling
    /// back unless <see cref="CommitAsync"/> ran, which is what makes every early return above
    /// safe.
    /// </summary>
    /// <remarks>
    /// Both halves are disposed, and that is not belt-and-braces. EF's enlistment does not own a
    /// transaction handed to it through <c>UseTransaction</c>, so disposing only the enlistment
    /// detaches the context and leaves <c>BEGIN IMMEDIATE</c> still holding the store's write lock
    /// on an open connection.
    /// </remarks>
    private sealed class WriteLock(SqliteTransaction transaction, IDbContextTransaction enlistment)
        : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) => enlistment.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await enlistment.DisposeAsync();
            await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The three columns a redemption decision reads, projected so the row is never materialised as
    /// an entity on a path that only needs to classify it.
    /// </summary>
    private sealed record InvitationState(
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RedeemedAt,
        DateTimeOffset? RevokedAt);
}
