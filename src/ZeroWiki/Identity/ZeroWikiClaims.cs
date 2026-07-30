namespace ZeroWiki.Identity;

/// <summary>Claim types this application issues beyond the framework's own.</summary>
public static class ZeroWikiClaims
{
    /// <summary>
    /// Present with the value <c>true</c> exactly when the account is an administrator (AD6).
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>ClaimTypes.Role</c>: AD6 keeps administrator status as a single
    /// boolean on the account and rules out a role model in this change. A policy over this
    /// claim gives later sections everything a role would, without implying a role table.
    /// </remarks>
    public const string IsAdministrator = "zerowiki:is_administrator";

    /// <summary>
    /// The only value of <see cref="IsAdministrator"/> that grants anything. Named rather than
    /// spelled out at each reader, because the claim has to be matched <em>on its value</em> — a
    /// check for the claim's mere presence reads <c>"false"</c> as an administrator.
    /// </summary>
    public const string AdministratorClaimValue = "true";
}
