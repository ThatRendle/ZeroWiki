using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 4.5 — "the system provides no open registration path". A negative about the whole
/// application, so it is asserted against the whole application: the routing table is enumerated
/// from the running host and every route is probed anonymously, rather than a handful of guessed
/// URLs being poked at.
/// </summary>
/// <remarks>
/// <para>
/// The difference matters. A test that fetched <c>/register</c> and <c>/signup</c> would pass
/// forever while saying nothing about the next route somebody adds; this one fails the moment any
/// new route is reachable without an account, which forces the addition to be a decision rather
/// than an accident.
/// </para>
/// <para>
/// Reachability is measured by <em>asking the running site</em>, not by reading
/// <c>[Authorize]</c> metadata off each endpoint. §6 denies anonymous access with a fallback policy
/// that lives in the authorization middleware's options and not in endpoint metadata, so a
/// metadata-driven version of this test would quietly stop meaning anything the moment §6 landed.
/// </para>
/// </remarks>
public sealed class NoOpenRegistrationTests : IDisposable
{
    private const string IssuerUsername = "alice";
    private const string Password = "a good long passphrase";
    private const string InvitationRedemptionRoute = $"{InvitationPolicy.RedemptionPath}/{{Token}}";

    /// <summary>
    /// Every route an anonymous visitor can reach today, and why the set is allowed to be this one.
    /// </summary>
    /// <remarks>
    /// Exactly two members can create an <see cref="Account"/>, and each is gated:
    /// <c>/bootstrap</c> is inert the instant any account exists, and <c>/invite/{Token}</c>
    /// refuses anything but a token matching a stored hash. Both gates are asserted below. The rest
    /// read, sign in, sign out, or report an error. A self-service registration route would have to
    /// be added to this list to make the suite green again.
    /// </remarks>
    private static readonly string[] AnonymouslyReachableRoutes =
    [
        "/",
        "/Error",
        "/_framework/opaque-redirect",
        "/bootstrap",
        "/bootstrap/complete",
        InvitationRedemptionRoute,
        "/login",
        "/logout",
        "/not-found",
    ];

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_routes_an_anonymous_visitor_can_reach_are_exactly_the_ones_named()
    {
        var reachable = new List<string>();

        foreach (var route in await RoutesAsync())
        {
            if (!IsDeniedToAnonymous(await _app.CreateHttpClient().GetAsync(ProbeUrl(route))))
            {
                reachable.Add(route);
            }
        }

        Assert.Equal(Ordered(AnonymouslyReachableRoutes), Ordered(reachable));
    }

    [Fact]
    public async Task Browsing_every_anonymous_route_creates_no_account()
    {
        foreach (var route in AnonymouslyReachableRoutes)
        {
            await _app.CreateHttpClient().GetAsync(ProbeUrl(route));
        }

        Assert.Empty(await _app.GetAccountsAsync());
    }

    [Fact]
    public async Task No_anonymous_page_asks_for_a_password_except_the_ones_that_must()
    {
        // The same claim from the other direction: self-service registration would have to collect
        // a password, so it cannot exist without showing up in one of these two scans.
        //
        // Two passes, because the two account-creating forms cannot both be live at once —
        // /bootstrap renders its form only while the store is empty, and /invite/{Token} needs an
        // invitation, which needs an issuer account, which is precisely what makes /bootstrap
        // inert. Login appears in both because signing in is not creating.
        Assert.Equal(["/bootstrap", "/login"], await PagesCollectingAPasswordAsync(token: "probe-token"));

        var token = await SeedInvitationAsync();

        Assert.Equal([InvitationRedemptionRoute, "/login"], await PagesCollectingAPasswordAsync(token));
    }

    [Fact]
    public async Task The_redemption_route_creates_nothing_without_a_token_that_matches()
    {
        // The gate on the first of the two account-creating routes, posted through the page's own
        // form so this is the real handler refusing rather than a missing route 404ing.
        var token = await SeedInvitationAsync();
        var client = _app.CreateHttpClient();
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(
            client,
            $"{InvitationPolicy.RedemptionPath}/{token}");

        foreach (var guess in new[] { new SecretTokenGenerator().Generate().Plaintext, "guess", "0" })
        {
            var response = await StaticSsrForm.PostAsync(
                client,
                $"{InvitationPolicy.RedemptionPath}/{guess}",
                fields.Concat(
                [
                    KeyValuePair.Create("Input.Username", "intruder"),
                    KeyValuePair.Create("Input.Password", Password),
                    KeyValuePair.Create("Input.ConfirmPassword", Password),
                ]));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(IssuerUsername, Assert.Single(await _app.GetAccountsAsync()).Username);
    }

    [Fact]
    public async Task The_bootstrap_route_creates_nothing_once_an_account_exists()
    {
        // The gate on the other one. §3 owns the behaviour; it is re-asserted here because 4.5's
        // claim is about the whole set, and a gate that stopped holding would make the claim false
        // without failing anything else in this file.
        await SeedInvitationAsync();

        var response = await _app.CreateHttpClient().GetAsync("/bootstrap");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(IssuerUsername, Assert.Single(await _app.GetAccountsAsync()).Username);
    }

    private static IEnumerable<string> Ordered(IEnumerable<string> routes) =>
        routes.Order(StringComparer.Ordinal);

    /// <summary>
    /// Whether an endpoint is one of the compiled static assets or the file fallback behind them.
    /// </summary>
    /// <remarks>
    /// Matched by type name because both marker types are <c>internal</c> to ASP.NET Core. That is
    /// a deliberately loose match, and it fails in the safe direction: a renamed marker would stop
    /// filtering and put dozens of asset routes in front of the assertion, which shows up as a
    /// loud failure rather than a route silently escaping the check.
    /// </remarks>
    private static bool IsAssetPlumbing(object metadata) =>
        metadata.GetType().Name is "StaticAssetDescriptor" or "FallbackMetadata";

    /// <summary>Substitutes the route's parameter so it can be fetched.</summary>
    private static string ProbeUrl(string route, string token = "probe-token") =>
        route.Replace("{Token}", token, StringComparison.Ordinal);

    /// <remarks>
    /// A 302 to <c>/login</c> is the shape a denial takes here, because the cookie handler
    /// challenges rather than returning 401. Anything else — including <c>/bootstrap</c>'s redirect
    /// to <c>/</c> when it is inert — counts as reached, which is the conservative direction: it
    /// puts more routes in front of the assertion, not fewer.
    /// </remarks>
    private static bool IsDeniedToAnonymous(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        if (response.StatusCode is not HttpStatusCode.Redirect || response.Headers.Location is not { } location)
        {
            return false;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(ZeroWikiAppFactory.BaseAddress, location);

        return target.AbsolutePath == "/login";
    }

    private async Task<IReadOnlyList<string>> PagesCollectingAPasswordAsync(string token)
    {
        var collecting = new List<string>();

        foreach (var route in AnonymouslyReachableRoutes)
        {
            var response = await _app.CreateHttpClient().GetAsync(ProbeUrl(route, token));

            if (response.IsSuccessStatusCode
                && (await response.Content.ReadAsStringAsync())
                    .Contains("""type="password" """, StringComparison.Ordinal))
            {
                collecting.Add(route);
            }
        }

        return [.. Ordered(collecting)];
    }

    /// <summary>
    /// Every route the running application serves, less the compiled static assets and the file
    /// fallback behind them.
    /// </summary>
    /// <remarks>
    /// Deliberately not filtered down to Razor component endpoints: a minimal API added with
    /// <c>MapPost("/register", …)</c> is exactly the thing this test exists to catch, and a filter
    /// that kept only components would step straight over it.
    /// </remarks>
    private async Task<IReadOnlyList<string>> RoutesAsync()
    {
        // Forces the host to start, so the endpoint data source is populated.
        await _app.CreateHttpClient().GetAsync("/");

        return
        [
            .. _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint => !endpoint.Metadata.Any(IsAssetPlumbing))
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Seeds an issuer and a live invitation, returning the plaintext token.</summary>
    private async Task<string> SeedInvitationAsync()
    {
        var secret = new SecretTokenGenerator().Generate();

        await _app.WithDbAsync(async db =>
        {
            var issuer = new Account
            {
                Id = Guid.NewGuid(),
                Username = IssuerUsername,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = IssuerUsername,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Accounts.Add(issuer);
            db.Invitations.Add(new Invitation
            {
                Id = Guid.NewGuid(),
                TokenHash = secret.Hash,
                IssuerAccountId = issuer.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow + InvitationPolicy.Lifetime,
            });

            await db.SaveChangesAsync();
        });

        return secret.Plaintext;
    }
}
