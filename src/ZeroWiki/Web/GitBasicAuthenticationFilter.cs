using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Primitives;
using ZeroWiki.Content;
using ZeroWiki.Identity;

namespace ZeroWiki.Web;

/// <summary>
/// D18 §4: the entire authentication decision for the git Smart HTTP routes. HTTP Basic carrying the
/// per-user git token, verified by <see cref="GitTokenService.VerifyAsync"/> — a database lookup with no
/// git subprocess involved — <b>before any subprocess exists</b>: a request that fails this check never
/// causes <see cref="GitHttpBackendHost"/> or its handler to run at all, because this method never calls
/// <c>next</c> when it does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a login password cannot authenticate here (structural, traced rather than asserted).</b>
/// <see cref="GitTokenService.VerifyAsync"/> hashes the presented value and looks it up only among
/// <c>GitTokens</c> rows matching the given username — it never reads <c>Accounts.PasswordHash</c>, so
/// there is no password code path to exclude, only a token path a password cannot enter.
/// </para>
/// <para>
/// <b>Attached to the exact same route registration as the three handlers</b>
/// (<see cref="GitSmartHttpEndpoints"/>) via <c>AddEndpointFilter</c> — the same mechanism
/// <c>[AllowAnonymous]</c> itself already uses, one level up (<c>Program.cs</c>'s
/// <c>FallbackPolicy</c>/<see cref="AnonymousGate"/>, one exemption list read twice by design). Because
/// routing evaluates the group's filters and its handler off the one matched <c>Endpoint</c>, "did this
/// request get Basic-auth-checked" and "did this request get handled by a git route" are the same
/// question, answered once — never a separately-matched path-prefix middleware that could disagree with
/// the routes it is meant to guard on a trailing slash, case, or an encoded segment (D18 §4, verified
/// with a throwaway minimal-API probe against exactly those vectors).
/// </para>
/// </remarks>
public static class GitBasicAuthenticationFilter
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the authenticated <see cref="AuthenticatedAccount"/> is
    /// stashed under for the handler (<see cref="GitSmartHttpEndpoints"/>) to read — set only on the
    /// path that goes on to call <c>next</c>, so its presence in <c>Items</c> is itself proof this filter
    /// ran and approved the request.
    /// </summary>
    public const string AuthenticatedAccountKey = "ZeroWiki.Git.AuthenticatedAccount";

    private const string Realm = "ZeroWiki";

    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!TryParseBasicCredentials(httpContext.Request.Headers.Authorization, out var username, out var token))
        {
            return Challenge(httpContext);
        }

        // Resolved per-request from RequestServices, not via AddEndpointFilter<T>'s generic overload:
        // that overload constructs the filter once, from the root service provider, at endpoint build
        // time -- unsafe for GitTokenService, which is Scoped (it depends on the per-request
        // IdentityDbContext). Resolving here, inside the per-request filter delegate, avoids the
        // captive-dependency hazard entirely.
        var gitTokens = httpContext.RequestServices.GetRequiredService<GitTokenService>();
        var account = await gitTokens.VerifyAsync(username, token, httpContext.RequestAborted);
        if (account is null)
        {
            return Challenge(httpContext);
        }

        httpContext.Items[AuthenticatedAccountKey] = account;
        return await next(context);
    }

    /// <summary>
    /// A <c>401</c> with <c>WWW-Authenticate: Basic realm="ZeroWiki"</c>. Returning an
    /// <see cref="IResult"/> here — rather than writing directly to the response and returning
    /// <see langword="null"/> — is what makes "never calls <c>next</c>" also mean "the handler's own
    /// body never runs and no subprocess is ever started" unambiguously: this method's control flow
    /// simply does not reach the line that would invoke it.
    /// </summary>
    /// <remarks>
    /// <b>Disables <see cref="IStatusCodePagesFeature"/> for this response — found by execution, not
    /// documentation.</b> <c>Program.cs</c>'s <c>UseStatusCodePagesWithReExecute("/not-found", ...)</c>
    /// wraps the entire downstream pipeline; a <c>401</c> with an empty body (what
    /// <see cref="Results.StatusCode(int)"/> produces) is exactly the shape that trips it, and it does
    /// not restore the original status code itself — the re-executed target does that, if it chooses
    /// to. The re-executed request lands on <c>/not-found</c> as a fresh, still-unauthenticated request,
    /// which <see cref="AnonymousGate"/> then answers with its own <c>200</c> landing page, discarding
    /// this method's <c>401</c> entirely (reproduced: an unauthenticated <c>curl</c> against a git route
    /// came back <c>200</c> with the anonymous landing page's HTML, <c>WWW-Authenticate</c> header still
    /// attached but the status silently replaced). A real git client needs the actual <c>401</c> to
    /// prompt for credentials; disabling the feature here is what keeps this response — an intentional,
    /// already-fully-formed refusal, not a missing page — out of that machinery.
    /// </remarks>
    private static IResult Challenge(HttpContext httpContext)
    {
        var statusCodePages = httpContext.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }

        httpContext.Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{Realm}\"";
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    private static bool TryParseBasicCredentials(StringValues authorizationHeader, out string? username, out string? token)
    {
        username = null;
        token = null;

        var value = authorizationHeader.ToString();
        if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value["Basic ".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(decoded);
        var separatorIndex = text.IndexOf(':');
        if (separatorIndex < 0)
        {
            return false;
        }

        username = text[..separatorIndex];
        token = text[(separatorIndex + 1)..];
        return true;
    }
}
