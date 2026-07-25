namespace ZeroWiki.Security;

/// <summary>
/// Hashes login passwords for storage and verifies a submitted password against a
/// stored hash. Implementations are one-way — a stored hash never yields the password.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes <paramref name="password"/> with a fresh random salt, returning a
    /// self-describing hash string suitable for storing in a single column.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against <paramref name="storedHash"/>. An
    /// absent, malformed, or unrecognised <paramref name="storedHash"/> is a failed
    /// verification, not an error — a corrupt stored value must not be distinguishable
    /// from a wrong password.
    /// </summary>
    bool Verify(string password, string? storedHash);
}
