using System.Security.Claims;

namespace ZeroWiki.Identity;

/// <summary>Reads ZeroWiki's own claims off a signed-in principal.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Whether the principal holds administrator rights (AD6).
    /// </summary>
    /// <remarks>
    /// Matched on the claim's <em>value</em>, never on its presence. The presence-only forms —
    /// <c>HasClaim(ZeroWikiClaims.IsAdministrator)</c>, or a policy built with the bare
    /// <c>RequireClaim(type)</c> overload — treat a claim of <c>"false"</c> as an administrator,
    /// which is the wrong answer in the direction that grants rather than denies. This lives in one
    /// place, and is tested, so that every caller inherits the correct comparison instead of
    /// re-deriving it and getting a coin flip.
    /// </remarks>
    public static bool IsAdministrator(this ClaimsPrincipal principal) =>
        principal.HasClaim(ZeroWikiClaims.IsAdministrator, ZeroWikiClaims.AdministratorClaimValue);
}
