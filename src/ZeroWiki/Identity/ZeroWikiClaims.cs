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
}
