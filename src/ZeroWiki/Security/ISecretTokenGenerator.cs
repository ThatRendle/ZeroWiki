namespace ZeroWiki.Security;

/// <summary>
/// Creates the high-entropy bearer secrets ZeroWiki hands out — git access tokens and
/// invitation tokens — and computes the hash a presented secret is looked up by.
/// </summary>
public interface ISecretTokenGenerator
{
    /// <summary>Generates a new secret together with the hash to persist for it.</summary>
    SecretToken Generate();

    /// <summary>
    /// Computes the at-rest hash of a presented secret, so a lookup can be done by hash
    /// without the store ever holding a usable secret.
    /// </summary>
    string ComputeHash(string plaintext);
}
