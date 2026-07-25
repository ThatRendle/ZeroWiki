namespace ZeroWiki.Security;

/// <summary>
/// A freshly generated secret. <see cref="Plaintext"/> is the caller's only copy — it is
/// shown to the user once and never persisted; <see cref="Hash"/> is what goes in the store.
/// </summary>
/// <param name="Plaintext">The secret to hand to the user exactly once.</param>
/// <param name="Hash">The at-rest hash of <paramref name="Plaintext"/>.</param>
public sealed record SecretToken(string Plaintext, string Hash);
