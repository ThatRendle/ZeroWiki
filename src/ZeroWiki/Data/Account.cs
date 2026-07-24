namespace ZeroWiki.Data;

/// <summary>
/// A registered user of the wiki. Created only by redeeming an <see cref="Invitation"/>
/// or via the one-time first-admin bootstrap.
/// </summary>
public sealed class Account
{
    public Guid Id { get; set; }

    /// <summary>Unique, compared case-insensitively (SQLite NOCASE collation).</summary>
    public required string Username { get; set; }

    /// <summary>Argon2id-encoded hash string. Never plaintext, never reversible.</summary>
    public required string PasswordHash { get; set; }

    public required string DisplayName { get; set; }

    public bool IsAdministrator { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<GitEmail> GitEmails { get; set; } = [];

    public ICollection<GitToken> GitTokens { get; set; } = [];
}
