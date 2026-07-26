using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Identity;

/// <summary>
/// Issues, lists, and revokes the single-use invitations that are the only way an account comes
/// into existence after the first administrator. Only token hashes are persisted.
/// </summary>
/// <remarks>
/// Every method takes the calling account's identity and decides for itself what that caller may
/// see and do (AD15). Scoping the query rather than filtering a rendered list is the point: a
/// filter in a view leaks through the next surface that reads the same data, and a route that
/// forgets to check cannot reach past this boundary.
/// </remarks>
public sealed class InvitationService(
    IdentityDbContext db,
    ISecretTokenGenerator tokenGenerator,
    TimeProvider timeProvider)
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
    public async Task<InvitationRevocation> RevokeAsync(
        Guid callerAccountId,
        bool callerIsAdministrator,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
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
        }

        return InvitationRevocation.Revoked;
    }
}
