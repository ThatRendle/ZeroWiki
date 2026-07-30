namespace ZeroWiki.Identity;

/// <summary>
/// An invitation as it appears in a list. Deliberately carries no token hash: the at-rest hash
/// has no business travelling into a render path.
/// </summary>
/// <param name="Id">Identifier used to revoke the invitation.</param>
/// <param name="IssuerAccountId">The account that issued it.</param>
/// <param name="IssuerUsername">That account's username, so an administrator's wider list is readable.</param>
/// <param name="CreatedAt">When the invitation was issued.</param>
/// <param name="ExpiresAt">When it stops being redeemable.</param>
/// <param name="RedeemedAt">When it created an account, or <see langword="null"/> if it has not.</param>
/// <param name="RevokedAt">When it was revoked, or <see langword="null"/> if it has not been.</param>
public sealed record InvitationSummary(
    Guid Id,
    Guid IssuerAccountId,
    string IssuerUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    DateTimeOffset? RevokedAt);
