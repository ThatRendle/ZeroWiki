using System.Globalization;
using System.Text;
using Konscious.Security.Cryptography;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Security;

public sealed class Argon2idPasswordHasherTests
{
    private const string Password = "correct horse battery staple";

    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Correct_password_verifies_against_its_hash()
    {
        var stored = _hasher.Hash(Password);

        Assert.True(_hasher.Verify(Password, stored));
    }

    [Fact]
    public void Wrong_password_does_not_verify()
    {
        var stored = _hasher.Hash(Password);

        Assert.False(_hasher.Verify("Correct horse battery staple", stored));
        Assert.False(_hasher.Verify(string.Empty, stored));
    }

    [Fact]
    public void An_absent_password_can_neither_be_hashed_nor_verified()
    {
        // Argon2 has no defined output for a zero-length password, so no stored hash can
        // ever correspond to one — verification must fail rather than throw.
        Assert.Throws<ArgumentException>(() => _hasher.Hash(string.Empty));
        Assert.Throws<ArgumentNullException>(() => _hasher.Hash(null!));

        var stored = _hasher.Hash(Password);
        Assert.False(_hasher.Verify(string.Empty, stored));
        Assert.False(_hasher.Verify(null!, stored));
        Assert.False(_hasher.Verify(Password, null));
    }

    [Fact]
    public void Stored_hash_is_not_the_password()
    {
        var stored = _hasher.Hash(Password);

        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_passwords_hash_differently_and_both_verify()
    {
        var first = _hasher.Hash(Password);
        var second = _hasher.Hash(Password);

        Assert.NotEqual(first, second);
        Assert.True(_hasher.Verify(Password, first));
        Assert.True(_hasher.Verify(Password, second));
    }

    [Fact]
    public void Hash_is_phc_encoded_with_the_configured_parameters()
    {
        var stored = _hasher.Hash(Password);

        var segments = stored.Split('$');
        Assert.Equal(6, segments.Length);
        Assert.Equal(string.Empty, segments[0]);
        Assert.Equal("argon2id", segments[1]);
        Assert.Equal("v=19", segments[2]);
        Assert.Equal("m=65536,t=3,p=1", segments[3]);

        // PHC base64 is unpadded, and the salt/digest are 16 and 32 bytes.
        Assert.DoesNotContain("=", segments[4], StringComparison.Ordinal);
        Assert.DoesNotContain("=", segments[5], StringComparison.Ordinal);
        Assert.Equal(16, DecodeB64(segments[4]).Length);
        Assert.Equal(32, DecodeB64(segments[5]).Length);
    }

    [Fact]
    public void Verify_uses_the_parameters_embedded_in_the_stored_hash()
    {
        // A hash produced with weaker-than-current parameters must still verify, so raising
        // the constants later does not lock existing accounts out.
        const int MemorySizeKib = 8192;
        const int Iterations = 1;
        var salt = Encoding.UTF8.GetBytes("sixteen-byte-slt");

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(Password))
        {
            Salt = salt,
            MemorySize = MemorySizeKib,
            Iterations = Iterations,
            DegreeOfParallelism = 1,
        };
        var digest = argon2.GetBytes(32);

        var stored = string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={MemorySizeKib},t={Iterations},p=1${EncodeB64(salt)}${EncodeB64(digest)}");

        Assert.True(_hasher.Verify(Password, stored));
        Assert.False(_hasher.Verify("wrong", stored));
    }

    [Fact]
    public void Verify_accepts_a_multi_lane_hash_that_sits_exactly_on_the_rfc_memory_bound()
    {
        // The m >= 8 * p bound must reject only what Argon2 itself cannot process: a hash
        // sitting exactly on it is legal and has to keep verifying.
        var salt = Encoding.UTF8.GetBytes("sixteen-byte-slt");

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(Password))
        {
            Salt = salt,
            MemorySize = 64,
            Iterations = 1,
            DegreeOfParallelism = 8,
        };
        var digest = argon2.GetBytes(32);

        var stored = $"$argon2id$v=19$m=64,t=1,p=8${EncodeB64(salt)}${EncodeB64(digest)}";

        Assert.True(_hasher.Verify(Password, stored));
    }

    [Fact]
    public void Tampered_digest_does_not_verify()
    {
        var segments = _hasher.Hash(Password).Split('$');
        var digest = segments[5].ToCharArray();
        digest[0] = digest[0] == 'A' ? 'B' : 'A';
        segments[5] = new string(digest);

        Assert.False(_hasher.Verify(Password, string.Join('$', segments)));
    }

    [Fact]
    public void Tampered_salt_does_not_verify()
    {
        var segments = _hasher.Hash(Password).Split('$');
        var salt = segments[4].ToCharArray();
        salt[0] = salt[0] == 'A' ? 'B' : 'A';
        segments[4] = new string(salt);

        Assert.False(_hasher.Verify(Password, string.Join('$', segments)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("plaintext-password-stored-by-mistake")]
    // Unknown algorithm.
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Unknown version.
    [InlineData("$argon2id$v=16$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Missing a cost parameter.
    [InlineData("$argon2id$v=19$m=65536,t=3$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Non-numeric cost parameter.
    [InlineData("$argon2id$v=19$m=lots,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Absurd memory cost — must be rejected, not allocated.
    [InlineData("$argon2id$v=19$m=999999999,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Beyond the accepted time cost.
    [InlineData("$argon2id$v=19$m=65536,t=32,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // One KiB above the accepted memory ceiling, and one lane above the accepted parallelism.
    [InlineData("$argon2id$v=19$m=262145,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=17$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Individually legal costs whose relation is not: RFC 9106 requires m >= 8 * p, and the KDF
    // itself throws on this rather than returning a digest. These pin the *contract* — malformed
    // input yields false, never an exception — not the mechanism: they pass whether the relation
    // check in TryParse rejects them or the catch in Verify absorbs the KDF's complaint, so do
    // not read them as evidence that the relation check itself is present.
    [InlineData("$argon2id$v=19$m=8,t=1,p=3$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=32,t=1,p=9$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    // Decodable but below the RFC length floors: a 4-byte salt, then an 8-byte digest.
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2g")]
    // Empty salt.
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$$aGFzaGhhc2hoYXNoaGFzaA")]
    // Salt is not valid base64.
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$!!!!$aGFzaGhhc2hoYXNoaGFzaA")]
    // Padded (non-canonical) base64.
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA==$aGFzaGhhc2hoYXNoaGFzaA")]
    // Truncated: digest segment missing.
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA")]
    public void Malformed_stored_hash_fails_verification_without_throwing(string stored)
    {
        Assert.False(_hasher.Verify(Password, stored));
    }

    private static string EncodeB64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] DecodeB64(string value)
    {
        var padding = (value.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(value + padding);
    }
}
