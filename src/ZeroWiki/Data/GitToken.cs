namespace ZeroWiki.Data;

/// <summary>
/// A revocable, high-entropy access token used as the Basic-auth password for the
/// git remote. The plaintext is shown once at creation and never stored — only its hash.
/// </summary>
public sealed class GitToken
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>SHA-256 hash of the token. High-entropy secret, so a fast hash is correct.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
