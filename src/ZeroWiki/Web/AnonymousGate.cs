using Microsoft.AspNetCore.Authorization;

namespace ZeroWiki.Web;

/// <summary>
/// Answers every unauthenticated request that has not opted out with <see cref="AllowAnonymousAttribute"/>
/// with <see cref="AnonymousLandingPage"/> (AD21).
/// </summary>
/// <remarks>
/// <para>
/// This is middleware rather than an authorization fallback policy because a fallback policy
/// <em>cannot</em> deliver AD21. A request that matches no endpoint carries no authorization
/// metadata, so the policy never runs: the request 404s, re-executes <c>/not-found</c>, and comes
/// back with a status a protected page's response does not have. The oracle then survives in the
/// status line however identical the bodies are. Middleware sees matched and unmatched routes
/// alike, so both leave here with the same status, the same bytes and the same headers.
/// </para>
/// <para>
/// The exemption is read from endpoint metadata rather than from a list of paths kept here, so the
/// set of anonymously reachable surfaces is stated once — as <c>[AllowAnonymous]</c> on the pages
/// that need it and <c>AllowAnonymous()</c> on the static assets — and the fallback policy behind
/// this middleware enforces the same list. Two lists would drift, and the drift would be silent in
/// the unsafe direction. It also gives §8 its seam: the git Smart HTTP routes opt out here and
/// answer with a real <c>401</c> plus <c>WWW-Authenticate</c>, without this mechanism changing.
/// </para>
/// <para>
/// Position is load-bearing twice over. It must run after <c>UseAuthentication()</c>, or
/// <c>User</c> is not yet populated and every signed-in member gets the anonymous page. It must run
/// after <c>UseRouting()</c>, or there is no endpoint to read the exemption from and even
/// <c>/login</c> is swallowed.
/// </para>
/// </remarks>
public sealed class AnonymousGate(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) =>
        context.User.Identity?.IsAuthenticated is true
        || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            ? next(context)
            : AnonymousLandingPage.WriteAsync(context);
}
