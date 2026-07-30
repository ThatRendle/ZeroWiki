namespace ZeroWiki.Identity;

/// <summary>
/// The account that claims a self-asserted git email (§8) — <b>not</b> an authenticated identity.
/// A commit's author email comes from <c>git config user.email</c> on the pusher's own machine, so
/// this record answers "which account currently holds this string in its email list", nothing more.
/// It deliberately carries no authority bit: attribution needs an account id and a username, not
/// a claim a credential check never verified.
/// </summary>
/// <param name="AccountId">The identifier of the account that owns the email.</param>
/// <param name="Username">The account's username, as stored.</param>
public sealed record GitEmailOwner(Guid AccountId, string Username);
