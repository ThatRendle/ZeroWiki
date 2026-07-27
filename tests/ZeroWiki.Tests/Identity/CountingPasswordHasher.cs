using System.Security.Cryptography;
using System.Text;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Identity;

/// <summary>
/// A cheap stand-in for Argon2id that records every derivation, so a test can assert <em>whether</em>
/// a hash was computed at all and <em>when</em> — neither of which is observable with the real one
/// without timing it.
/// </summary>
/// <remarks>
/// <para>
/// Two properties are being tested through this. That an anonymous caller cannot make the server
/// derive a 64 MiB hash by presenting a token that was never going to work (BL1: every rejection
/// must leave <see cref="Derivations"/> empty). And that the derivation happens outside the store's
/// write lock — <see cref="OnHash"/> is the hook a test uses to look at the lock while a hash is in
/// flight.
/// </para>
/// <para>
/// The stored form is a digest, not the password: a double that echoed its input would make
/// "the plaintext is never persisted" pass against a store that was in fact holding it.
/// </para>
/// </remarks>
public sealed class CountingPasswordHasher : IPasswordHasher
{
    private readonly List<string> _derivations = [];

    /// <summary>Every password passed to <see cref="Hash"/>, in order.</summary>
    public IReadOnlyList<string> Derivations => _derivations;

    /// <summary>Runs inside <see cref="Hash"/>, before it returns.</summary>
    public Action? OnHash { get; set; }

    /// <summary>
    /// Discards the derivations so far, so a test can set up through paths that legitimately hash
    /// and then assert that the paths it is actually about hash nothing.
    /// </summary>
    public void Forget() => _derivations.Clear();

    public string Hash(string password)
    {
        _derivations.Add(password);
        OnHash?.Invoke();

        return Digest(password);
    }

    public bool Verify(string password, string? storedHash) =>
        CanVerify(storedHash) && Digest(password) == storedHash;

    public bool CanVerify(string? storedHash) =>
        storedHash?.StartsWith("$stub$", StringComparison.Ordinal) == true;

    private static string Digest(string password) =>
        $"$stub${Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)))}";
}
