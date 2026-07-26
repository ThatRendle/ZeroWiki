namespace ZeroWiki.Identity;

/// <summary>
/// The identity established by a successful login. Deliberately the projection the credential
/// check reads and nothing more — no timestamps, no collections, and never the password hash.
/// </summary>
/// <param name="Id">The account's identifier.</param>
/// <param name="Username">The account's username, as stored.</param>
/// <param name="IsAdministrator">Whether the account holds administrator rights (AD6).</param>
public sealed record AuthenticatedAccount(Guid Id, string Username, bool IsAdministrator);
