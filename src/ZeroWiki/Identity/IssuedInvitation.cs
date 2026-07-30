namespace ZeroWiki.Identity;

/// <summary>
/// The result of issuing an invitation. <see cref="Token"/> is the plaintext and this is the
/// only place it ever exists — the store holds nothing but its hash — so it must be shown to
/// the issuer now or lost.
/// </summary>
/// <param name="Id">Identifier of the stored invitation, used to revoke it later.</param>
/// <param name="Token">The plaintext redemption token, shown exactly once.</param>
/// <param name="CreatedAt">When the invitation was issued.</param>
/// <param name="ExpiresAt">When the invitation stops being redeemable, fixed at issue.</param>
public sealed record IssuedInvitation(Guid Id, string Token, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
