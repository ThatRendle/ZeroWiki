using System.Security.Cryptography;
using System.Text;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Security;

public sealed class SecretTokenGeneratorTests
{
    private readonly SecretTokenGenerator _generator = new();

    [Fact]
    public void Generated_plaintext_is_unpadded_base64url_of_256_bits()
    {
        var token = _generator.Generate();

        Assert.Equal(43, token.Plaintext.Length);
        Assert.All(
            token.Plaintext,
            c => Assert.True(
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
                $"'{c}' is not a base64url character."));
    }

    [Fact]
    public void Generated_secrets_are_distinct()
    {
        var plaintexts = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 200; i++)
        {
            var token = _generator.Generate();
            Assert.True(plaintexts.Add(token.Plaintext));
            Assert.True(hashes.Add(token.Hash));
        }
    }

    [Fact]
    public void Hash_is_the_lowercase_hex_sha256_of_the_plaintext()
    {
        var token = _generator.Generate();

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Plaintext)))
            .ToLowerInvariant();

        Assert.Equal(expected, token.Hash);
        Assert.Equal(64, token.Hash.Length);
    }

    [Fact]
    public void Hash_does_not_reveal_the_plaintext()
    {
        var token = _generator.Generate();

        Assert.DoesNotContain(token.Plaintext, token.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeHash_reproduces_the_hash_of_a_presented_secret()
    {
        var token = _generator.Generate();

        Assert.Equal(token.Hash, _generator.ComputeHash(token.Plaintext));
        Assert.NotEqual(token.Hash, _generator.ComputeHash(token.Plaintext + "x"));
    }

    [Fact]
    public void ComputeHash_rejects_an_absent_secret()
    {
        Assert.Throws<ArgumentException>(() => _generator.ComputeHash(string.Empty));
        Assert.Throws<ArgumentNullException>(() => _generator.ComputeHash(null!));
    }
}
