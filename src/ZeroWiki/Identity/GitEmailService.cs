using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;

namespace ZeroWiki.Identity;

/// <summary>
/// Manages the git emails a member associates with their own account — the addresses a pushed
/// commit's author line will eventually be matched against to attribute it back to an account
/// (§8). This service only manages the list; resolving an email to an account at push time is
/// §8's own concern and is not implemented here.
/// </summary>
/// <remarks>
/// Every method is scoped by the caller's own account id, taken by the caller
/// (<see cref="Components.Pages.Account"/>) from the signed-in principal and never from the
/// request body — the same shape as <see cref="GitTokenService.RevokeAsync"/>. "Zero associated
/// emails" is an explicitly legal account state (the account-model spec), so nothing here stops
/// the last one from being removed.
/// </remarks>
public sealed class GitEmailService(IdentityDbContext db)
{
    /// <summary>
    /// The <c>GitEmails.Email</c> column width
    /// (<see cref="Data.Configurations.GitEmailConfiguration"/>).
    /// </summary>
    public const int MaximumEmailLength = 320;

    /// <summary>Associates an email with the caller's account.</summary>
    /// <remarks>
    /// <para>
    /// Validated structurally rather than with a pattern (AD26): trimmed, capped at
    /// <see cref="MaximumEmailLength"/>, and required to hold exactly one <c>@</c> with at least
    /// one character on either side. Both scans are linear in the trimmed length, so — unlike
    /// the <c>[EmailAddress]</c>/regex route this deliberately avoids — nothing here can be made
    /// to backtrack. §3's BL2 already found that a validation regex run ahead of the handler
    /// reinstated a DoS amplifier through an earlier door; this sidesteps that shape entirely by
    /// not using a pattern at all.
    /// </para>
    /// <para>
    /// The uniqueness check runs before the insert and, if two identical submissions still race
    /// past it, is re-run after a failed insert rather than left to surface as an unhandled
    /// exception — so a legitimate double-submit (a double click, two open tabs) reports one of
    /// the two true outcomes below instead of an error page. <c>GitEmails.Email</c>'s
    /// <c>NOCASE</c> unique index is the only constraint either query or insert can hit here.
    /// </para>
    /// </remarks>
    public async Task<GitEmailAddOutcome> AddAsync(
        Guid accountId,
        string? rawEmail,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(rawEmail, out var email))
        {
            return GitEmailAddOutcome.Malformed;
        }

        var owner = await FindOwnerAsync(email, cancellationToken);
        if (owner is not null)
        {
            return Outcome(owner.Value, accountId);
        }

        var candidate = new GitEmail
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Email = email,
        };

        db.GitEmails.Add(candidate);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(candidate).State = EntityState.Detached;

            owner = await FindOwnerAsync(email, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The insert of a git email failed as a duplicate, but no row now claims that address.");

            return Outcome(owner.Value, accountId);
        }

        return GitEmailAddOutcome.Added;
    }

    /// <summary>Lists the caller's own git emails, alphabetically.</summary>
    public async Task<IReadOnlyList<GitEmailSummary>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        await db.GitEmails
            .AsNoTracking()
            .Where(e => e.AccountId == accountId)
            .OrderBy(e => e.Email)
            .Select(e => new GitEmailSummary(e.Id, e.Email))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Removes one of the caller's own git emails. Returns <see langword="false"/> only when the
    /// caller has no such email — which also covers naming another account's, so this cannot be
    /// used to learn whether an identifier belongs to somebody else. Removing the last remaining
    /// email is allowed: "zero associated emails" is a legal account state.
    /// </summary>
    public async Task<bool> RemoveAsync(
        Guid accountId,
        Guid emailId,
        CancellationToken cancellationToken = default)
    {
        var email = await db.GitEmails
            .SingleOrDefaultAsync(e => e.Id == emailId && e.AccountId == accountId, cancellationToken);

        if (email is null)
        {
            return false;
        }

        db.GitEmails.Remove(email);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static GitEmailAddOutcome Outcome(Guid owningAccountId, Guid callerAccountId) =>
        owningAccountId == callerAccountId
            ? GitEmailAddOutcome.AlreadyOnThisAccount
            : GitEmailAddOutcome.TakenByAnotherAccount;

    /// <summary>
    /// The account that currently holds <paramref name="email"/>, or <see langword="null"/> when
    /// none does. The column collates <c>NOCASE</c>, so this equality comparison is already the
    /// case-insensitive match the unique index enforces — there is no separate normalisation to
    /// keep in step with it.
    /// </summary>
    private async Task<Guid?> FindOwnerAsync(string email, CancellationToken cancellationToken) =>
        await db.GitEmails
            .AsNoTracking()
            .Where(e => e.Email == email)
            .Select(e => (Guid?)e.AccountId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Trims and structurally validates a candidate address without a regular expression
    /// (AD26): exactly one <c>@</c> with at least one character on either side, within the
    /// length the column allows.
    /// </summary>
    private static bool TryNormalize(string? raw, out string email)
    {
        email = string.Empty;

        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaximumEmailLength)
        {
            return false;
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1 || at != trimmed.LastIndexOf('@'))
        {
            return false;
        }

        email = trimmed;
        return true;
    }
}
