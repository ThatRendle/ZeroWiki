using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ZeroWiki.Identity;

/// <summary>
/// Reads the currently signed-in account straight off the request's <see cref="ClaimsPrincipal"/>
/// (§8.3).
/// </summary>
/// <remarks>
/// A <b>principal-only handle</b>, by Product Owner decision (2026-07-30): it reads only the
/// claims <see cref="Components.Pages.Login"/> already mints at sign-in — <c>Id</c>,
/// <c>Username</c>, <c>IsAdministrator</c> — and performs no database read. It deliberately does
/// not carry a <c>DisplayName</c> or a git email; a caller that needs either for something like a
/// commit author line has to ask for that as a separate primitive, because minting those into the
/// session or reading them here was explicitly ruled out of this section's scope.
/// </remarks>
public sealed class CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// The signed-in account, or <see langword="null"/> for an anonymous visitor (or when called
    /// outside a request).
    /// </summary>
    public AuthenticatedAccount? GetCurrent()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = user.FindFirstValue(ClaimTypes.Name);
        if (id is null || username is null || !Guid.TryParse(id, out var accountId))
        {
            return null;
        }

        return new AuthenticatedAccount(accountId, username, user.IsAdministrator());
    }
}
