namespace ZeroWiki.Data;

/// <summary>
/// A single-use, expiring invitation that lets an invitee create an account.
/// State (unredeemed / redeemed / revoked / expired) is derived from
/// <see cref="RedeemedAt"/>, <see cref="RevokedAt"/>, and <see cref="ExpiresAt"/> —
/// there is no redundant status enum.
/// </summary>
public sealed class Invitation
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hash of the invitation token. The plaintext is shown once and never stored.</summary>
    public required string TokenHash { get; set; }

    public Guid IssuerAccountId { get; set; }

    public Account? IssuerAccount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
