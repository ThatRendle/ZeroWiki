using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;

namespace ZeroWiki.Identity;

/// <summary>
/// Manages the git emails a member associates with their own account, and resolves one back to
/// its owning account (§8) — the addresses a pushed commit's author line is matched against to
/// attribute it. Parsing the commit and driving the git remote is git-backed-content-core's own
/// concern and is not implemented here.
/// </summary>
/// <remarks>
/// <para>
/// The account-page methods — <see cref="AddAsync"/>, <see cref="ListAsync"/>, and
/// <see cref="RemoveAsync"/> — are each scoped by the caller's own account id, taken by the caller
/// (<see cref="Components.Pages.Account"/>) from the signed-in principal and never from the
/// request body — the same shape as <see cref="GitTokenService.RevokeAsync"/>. "Zero associated
/// emails" is an explicitly legal account state (the account-model spec), so nothing here stops
/// the last one from being removed.
/// </para>
/// <para>
/// <see cref="FindByEmailAsync"/> is the deliberate exception to that scoping: it takes no account
/// id at all and resolves an arbitrary email to whichever account currently claims it. That is by
/// design — §8's account-lookup requirement is unscoped by definition — but it means the method's
/// input is untrusted (a commit's author email, self-asserted by the pusher) and its output must
/// never be surfaced to a user the way <see cref="AddAsync"/>'s outcome is. §7's spec forbids the
/// add flow from revealing which account holds an address ("without identifying which account");
/// <see cref="AddAsync"/> still honours that because it reads only <c>owner.AccountId</c> to decide
/// between <see cref="GitEmailAddOutcome.AlreadyOnThisAccount"/> and
/// <see cref="GitEmailAddOutcome.TakenByAnotherAccount"/> — the identity itself never leaves this
/// class on that path.
/// </para>
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

        var owner = await FindByEmailAsync(email, cancellationToken);
        if (owner is not null)
        {
            return Outcome(owner.AccountId, accountId);
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

            owner = await FindByEmailAsync(email, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The insert of a git email failed as a duplicate, but no row now claims that address.");

            return Outcome(owner.AccountId, accountId);
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
    /// <remarks>
    /// Callers must pass <see cref="CancellationToken.None"/> here, not the request's own token
    /// (D1): abandoning this on disconnect would leave the member believing an address is
    /// disassociated while it still attributes their commits.
    /// </remarks>
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

    /// <summary>
    /// Resolves a git email to the account that claims it (§8), or <see langword="null"/> when no
    /// account holds it — a value, not an exception, per the account-lookup requirement. This is a
    /// lookup on self-asserted input, not a credential check: the caller has proven nothing about
    /// the account this returns, only that some account's git-email list contains the string
    /// passed in. See <see cref="GitEmailOwner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>AddAsync</c>'s own uniqueness lookup, promoted rather than duplicated: §7's
    /// reviewer flagged the adjacency of a private-only lookup here and §8's account-lookup
    /// primitive, and a second near-identical private method would only let the two drift. The
    /// one extra join this costs over the account-id-only shape <c>AddAsync</c> used before is
    /// negligible against a single-row lookup by unique index.
    /// </para>
    /// <para>
    /// The column collates <c>NOCASE</c>, so this equality comparison is already the
    /// case-insensitive match the unique index enforces — there is no separate normalisation to
    /// keep in step with it, and none is applied here on purpose (AD26 scoped that requirement to
    /// §7.2's authority over storage; §8 supervisor finding S1 extends "the database is the
    /// authority" to every place a git email is matched, including this one).
    /// </para>
    /// <para>
    /// Projected into <see cref="GitEmailOwner"/> rather than materialised as an
    /// <see cref="Account"/> (AD7) — a corrupt row elsewhere on the account must not turn this
    /// lookup into a 500 — and rather than into <see cref="AuthenticatedAccount"/> (§8 supervisor
    /// finding S2): the result carries no <c>IsAdministrator</c> bit, because an authority flag
    /// derived from a commit's author line would be surplus authority sourced from untrusted input.
    /// </para>
    /// </remarks>
    public async Task<GitEmailOwner?> FindByEmailAsync(
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        return await db.GitEmails
            .AsNoTracking()
            .Where(e => e.Email == email)
            .Select(e => new GitEmailOwner(e.Account!.Id, e.Account!.Username))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static GitEmailAddOutcome Outcome(Guid owningAccountId, Guid callerAccountId) =>
        owningAccountId == callerAccountId
            ? GitEmailAddOutcome.AlreadyOnThisAccount
            : GitEmailAddOutcome.TakenByAnotherAccount;

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
