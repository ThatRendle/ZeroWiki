namespace ZeroWiki.Identity;

/// <summary>
/// The result of issuing a git access token. <see cref="Token"/> is the plaintext and this
/// is the only place it ever exists — it is not stored and cannot be recovered, so it must
/// be shown to the user now or lost.
/// </summary>
/// <param name="Id">Identifier of the stored token, used to revoke it later.</param>
/// <param name="Token">The plaintext token value, shown exactly once.</param>
/// <param name="CreatedAt">When the token was issued.</param>
public sealed record IssuedGitToken(Guid Id, string Token, DateTimeOffset CreatedAt);
