using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// Wraps a real hasher and records every verification, so tests can assert that the same work
/// happened on paths that have already decided to fail.
/// </summary>
/// <remarks>
/// <para>
/// This is how timing uniformity is asserted here: a wall-clock comparison would be flaky under
/// load and would rot as the parameters change, whereas "exactly one verification, of this
/// password, against this hash" is the property that actually has to hold.
/// </para>
/// <para>
/// Recording <em>both</em> arguments is load-bearing. Recording only the stored hash lets a miss
/// path keep its single verification against the right dummy while quietly passing an empty
/// password — which costs nothing to verify, and is precisely the free miss path the dummy hash
/// exists to prevent. Measured: an empty password against the dummy returns in 0.0 ms, against
/// 220 ms for a real one.
/// </para>
/// </remarks>
public sealed class RecordingPasswordHasher(IPasswordHasher inner) : IPasswordHasher
{
    private readonly List<Verification> _verifications = [];

    /// <summary>Every <see cref="Verify"/> call, in order.</summary>
    public IReadOnlyList<Verification> Verifications => _verifications;

    /// <summary>The stored hash passed to each <see cref="Verify"/> call, in order.</summary>
    public IReadOnlyList<string?> VerifiedAgainst => [.. _verifications.Select(v => v.StoredHash)];

    public string Hash(string password) => inner.Hash(password);

    public bool Verify(string password, string? storedHash)
    {
        _verifications.Add(new Verification(password, storedHash));
        return inner.Verify(password, storedHash);
    }

    public bool CanVerify(string? storedHash) => inner.CanVerify(storedHash);

    /// <summary>
    /// One call to <see cref="Verify"/>. Test-only, and it holds the submitted password —
    /// nothing like this belongs in the application.
    /// </summary>
    public sealed record Verification(string Password, string? StoredHash);
}
