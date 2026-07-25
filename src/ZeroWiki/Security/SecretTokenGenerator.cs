using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ZeroWiki.Security;

/// <summary>
/// Generates 256-bit cryptographically random secrets, encoded base64url without padding
/// so they are safe both as a Basic-auth password and inside a URL, and hashes them with
/// SHA-256 as lowercase hex.
/// </summary>
/// <remarks>
/// A fast hash is the correct choice here, unlike for passwords: the input is full-entropy
/// random, so there is no guessable keyspace for an attacker to grind through. Lowercase
/// hex keeps the encoding canonical, which matters because the store looks these up
/// through a case-sensitive unique index.
/// </remarks>
public sealed class SecretTokenGenerator : ISecretTokenGenerator
{
    /// <summary>256 bits of entropy — 43 characters once base64url-encoded.</summary>
    private const int SecretLength = 32;

    public SecretToken Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        try
        {
            var plaintext = Base64Url.EncodeToString(secret);
            return new SecretToken(plaintext, ComputeHash(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public string ComputeHash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
    }
}
