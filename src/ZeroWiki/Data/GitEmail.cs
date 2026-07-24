namespace ZeroWiki.Data;

/// <summary>
/// A git author email associated with an account, used to attribute push-originated
/// commits back to the account that made them.
/// </summary>
public sealed class GitEmail
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>Unique across all accounts — an email resolves to exactly one account.</summary>
    public required string Email { get; set; }
}
