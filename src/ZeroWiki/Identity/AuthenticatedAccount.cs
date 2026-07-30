namespace ZeroWiki.Identity;

/// <summary>
/// The identity established by a successful login. Deliberately the projection the credential
/// check reads and nothing more — no timestamps, no collections, and never the password hash.
/// </summary>
/// <remarks>
/// Produced only by a credential check (<see cref="GitTokenService.VerifyAsync"/>) or read back
/// from an already-established session (<see cref="CurrentUserAccessor.GetCurrent"/>) — never by a
/// lookup keyed on unverified, self-asserted input. A value derived from such input, however
/// account-shaped, is a <see cref="GitEmailOwner"/> (or similar), not this type.
/// </remarks>
/// <param name="Id">The account's identifier.</param>
/// <param name="Username">The account's username, as stored.</param>
/// <param name="IsAdministrator">Whether the account holds administrator rights (AD6).</param>
public sealed record AuthenticatedAccount(Guid Id, string Username, bool IsAdministrator);
