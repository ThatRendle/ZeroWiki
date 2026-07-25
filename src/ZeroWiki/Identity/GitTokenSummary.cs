namespace ZeroWiki.Identity;

/// <summary>
/// A git access token as its owner sees it in a list. Deliberately carries no hash: the
/// at-rest hash has no business travelling into a render path.
/// </summary>
/// <param name="Id">Identifier used to revoke the token.</param>
/// <param name="CreatedAt">When the token was issued.</param>
/// <param name="RevokedAt">When the token was revoked, or <see langword="null"/> if it is still valid.</param>
public sealed record GitTokenSummary(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
