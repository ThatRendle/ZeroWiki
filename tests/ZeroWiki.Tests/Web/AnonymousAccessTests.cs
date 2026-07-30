using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;
using ZeroWiki.Web;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Tasks 6.1 and 6.2 — what an unauthenticated visitor can see, and what they can learn from
/// asking. Exercised over HTTP because the property AD21 asks for is about whole responses:
/// status, body and headers together.
/// </summary>
/// <remarks>
/// Half of this file is about the authenticated path, deliberately. A suite that only asserts
/// "anonymous is denied" stays green through a break that denies <em>everyone</em> — removing
/// <c>UseAuthorization()</c> once bounced every signed-in member to <c>/login</c> while both
/// anonymous tests passed (AD16). Each anonymous claim below therefore has a member-side twin.
/// </remarks>
public sealed partial class AnonymousAccessTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    /// <summary>Exists and requires a session.</summary>
    private const string ProtectedUrl = "/invitations";

    /// <summary>Matches no endpoint at all, so nothing carries authorization metadata for it.</summary>
    private const string NonExistentUrl = "/definitely-not-a-page";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_protected_url_and_a_url_that_does_not_exist_are_identical()
    {
        // The central test of §6. A stranger who can tell these two apart can map the wiki by
        // probing names, which is the oracle AD21 closes — and the status line is where it hides,
        // because a request matching no endpoint 404s through the status-code pages while a
        // matched-but-protected one does not.
        var app = _app.CreateHttpClient();
        var missing = _app.CreateHttpClient();

        var guarded = await app.GetAsync(ProtectedUrl);
        var absent = await missing.GetAsync(NonExistentUrl);

        Assert.Equal(HttpStatusCode.OK, guarded.StatusCode);
        Assert.Equal(guarded.StatusCode, absent.StatusCode);
        Assert.Equal(
            await guarded.Content.ReadAsStringAsync(),
            await absent.Content.ReadAsStringAsync());
        Assert.Equal(ComparableHeaders(guarded), ComparableHeaders(absent));

        // And the shared response is the landing page rather than, say, both leaking the guarded
        // page: identical is necessary, not sufficient.
        await HttpAssertions.AssertIsAnonymousLandingPageAsync(guarded);
    }

    [Theory]
    [InlineData("/")]
    [InlineData(ProtectedUrl)]
    [InlineData(NonExistentUrl)]
    [InlineData("/logout")]
    [InlineData("/not-found")]
    [InlineData("/invite")]
    [InlineData("/bootstrap/complete/deeper")]
    [InlineData("/_framework/opaque-redirect")]
    [InlineData("/Invitations?returnUrl=/x&q=1")]
    public async Task Every_non_exempt_url_answers_an_anonymous_visitor_with_the_same_page(string url) =>
        await HttpAssertions.AssertIsAnonymousLandingPageAsync(await _app.CreateHttpClient().GetAsync(url));

    [Fact]
    public async Task An_anonymous_post_gets_the_same_page_as_an_anonymous_get()
    {
        // The gate runs ahead of antiforgery, so a post is refused with the same page rather than
        // with a 400 that would say "this URL exists and has a form on it".
        var posted = await StaticSsrForm.PostAsync(
            _app.CreateHttpClient(),
            ProtectedUrl,
            [KeyValuePair.Create("_handler", "issue-invitation")]);

        await HttpAssertions.AssertIsAnonymousLandingPageAsync(posted);
    }

    [Fact]
    public async Task The_anonymous_home_page_offers_a_login_link_and_nothing_else()
    {
        // Task 6.1 stated as the spec states it: "exposes only a Login link", with no content and
        // no navigation. Counting the anchors is what makes "only" mean something.
        var response = await _app.CreateHttpClient().GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        await HttpAssertions.AssertIsAnonymousLandingPageAsync(response);

        var link = Assert.Single(AnchorTag().Matches(body)).Value;
        Assert.Contains("""href="/login">""", link, StringComparison.Ordinal);

        Assert.DoesNotContain("nav-scrollable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello, world!", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_anonymous_page_never_says_whether_the_wiki_has_any_accounts()
    {
        // §3 was careful that nothing routes an empty store to /bootstrap or announces that it is
        // open; a landing page that varied with the account count would undo that.
        using var populated = new ZeroWikiAppFactory();
        await SeedAccountAsync(populated);

        var empty = await _app.CreateHttpClient().GetAsync("/");
        var seeded = await populated.CreateHttpClient().GetAsync("/");

        Assert.Empty(await _app.GetAccountsAsync());
        Assert.NotEmpty(await populated.GetAccountsAsync());

        Assert.Equal(
            await empty.Content.ReadAsStringAsync(),
            await seeded.Content.ReadAsStringAsync());
        // Structurally, not by looking for words: the page's only link is to sign in, so it cannot
        // be offering /bootstrap to the first visitor of an empty deployment.
        Assert.All(
            AnchorTag().Matches(AnonymousLandingPage.Html),
            link => Assert.Contains("""href="/login">""", link.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_anonymous_request_is_redirected_to_login()
    {
        // The fallback policy underneath the gate challenges with a 302 to /login. If the gate ever
        // stops running first, every response below changes shape — and this is the assertion that
        // notices, because "identical to each other" would still be true of a pile of redirects.
        foreach (var url in new[] { "/", ProtectedUrl, NonExistentUrl, "/logout", "/not-found" })
        {
            var response = await _app.CreateHttpClient().GetAsync(url);

            Assert.False(
                response.StatusCode is HttpStatusCode.Redirect,
                $"'{url}' redirected an anonymous visitor to {response.Headers.Location}.");
        }
    }

    [Fact]
    public async Task The_anonymous_response_carries_exactly_the_headers_it_declares()
    {
        // Pinned absolutely, not relative to a second response. Comparing the two responses only to
        // each other says they match, never what they are — deleting the explicit ContentLength
        // left both sides equally unframed and survived the whole suite.
        //
        // AD21's "the app emits no cache directives" is asserted the same way: by the header set
        // being exactly these two, so a Cache-Control added later fails here rather than needing
        // someone to have predicted its name.
        var response = await _app.CreateHttpClient().GetAsync(ProtectedUrl);

        Assert.Equal(
            ["Content-Length", "Content-Type"],
            ComparableHeaders(response).Select(header => header.Key));

        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        // The declared length is the delivered length: a Content-Length that drifts from the body
        // is a response-splitting shape, and an absent one changes the framing of a response whose
        // whole point is being identical to another.
        Assert.Equal(
            (await response.Content.ReadAsByteArrayAsync()).LongLength,
            response.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/bootstrap")]
    [InlineData("/bootstrap/complete")]
    [InlineData("/Error")]
    public async Task An_exempt_page_is_still_reachable_anonymously(string url) =>
        await HttpAssertions.AssertIsNotAnonymousLandingPageAsync(await _app.CreateHttpClient().GetAsync(url));

    [Fact]
    public async Task The_invitation_redemption_page_is_still_reachable_anonymously()
    {
        // The load-bearing exemption: the invitee has no account until this page creates one, so a
        // swallowed redemption route closes the only door §4 opened.
        var token = await SeedInvitationAsync();

        var response = await _app.CreateHttpClient()
            .GetAsync($"{InvitationPolicy.RedemptionPath}/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Accept your invitation",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    public async Task A_stylesheet_the_anonymous_pages_link_is_served_anonymously(string url)
    {
        // Static assets are endpoints too. Swallowing them would leave the login page unstyled for
        // exactly the visitors who need it, and both anonymous pages link these two by name.
        var response = await _app.CreateHttpClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(url, AnonymousLandingPage.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_member_gets_the_real_page()
    {
        // Hazard 2 (AD16): a break that denies everyone leaves every anonymous assertion above
        // green. This is the twin that dies instead.
        await SeedAccountAsync(_app);
        var client = await SignInAsync();

        var response = await client.GetAsync(ProtectedUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await HttpAssertions.AssertIsNotAnonymousLandingPageAsync(response);
        Assert.Contains(
            "Create an invitation",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_member_still_gets_a_not_found_for_a_url_that_does_not_exist()
    {
        // The other half of the same hazard: the gate must apply to anonymous callers only, so a
        // member keeps an honest 404 rather than a 200 that hides their typo.
        await SeedAccountAsync(_app);
        var client = await SignInAsync();

        var response = await client.GetAsync(NonExistentUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "does not exist",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/bootstrap")]
    [InlineData("/Error")]
    [InlineData("/invite/no-such-token")]
    public async Task An_anonymously_reachable_page_links_nowhere_but_the_site_root(string url)
    {
        // "SHALL NOT expose … navigation to anonymous visitors" is about every page a stranger can
        // reach, not only the landing page — and it is asserted here by counting anchors rather
        // than by looking for the class name of a menu that happens to be today's navigation. The
        // weaker form missed a project-template "About" link to learn.microsoft.com sitting in the
        // layout, outside the menu, on all four of these pages.
        var tags = AnchorTag().Matches(await _app.CreateHttpClient().GetStringAsync(url))
            .Select(match => match.Value)
            .ToList();

        var brand = Assert.Single(tags);
        Assert.Contains("navbar-brand", brand, StringComparison.Ordinal);

        // Where it points is asserted positively, against the boundary the project already trusts,
        // rather than by listing spellings a hostile href must not have. Two negatives ("no ://",
        // "no target=") both accept `//evil.example`, which a browser resolves against the current
        // scheme — the same protocol-relative shape that slipped past §5's redirect assertion and
        // past the first version of this test. An empty href is the normal rendering here and is
        // local by construction: it resolves against <base href="/">.
        var href = HrefAttribute().Match(brand);
        Assert.True(href.Success, $"'{url}' renders an anchor with no href at all: {brand}");

        var target = href.Groups["href"].Value;
        Assert.True(
            string.IsNullOrEmpty(target) || LocalUrl.IsLocal(target),
            $"'{url}' offers an anonymous visitor a link off this origin: {brand}");
    }

    [Fact]
    public async Task The_navigation_appears_for_a_member_and_for_nobody_else()
    {
        // The member-side twin of the audit above: hiding navigation from strangers must not be
        // achieved by hiding it from everybody.
        await SeedAccountAsync(_app);

        var anonymous = await _app.CreateHttpClient().GetStringAsync("/login");
        Assert.DoesNotContain("nav-scrollable", anonymous, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign out", anonymous, StringComparison.Ordinal);

        var member = await (await SignInAsync()).GetStringAsync("/");
        Assert.Contains("nav-scrollable", member, StringComparison.Ordinal);
        Assert.Contains($"Sign out {Username}", member, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_login_link_is_bare_and_the_script_builds_its_return_url_from_the_path_only()
    {
        // With scripting off the link stays /login and sign-in lands home: degraded, never broken.
        // With scripting on the script must write pathname + search and never href, because href
        // carries an origin and this value is repeated back into a redirect target.
        var body = await _app.CreateHttpClient().GetStringAsync(ProtectedUrl);

        Assert.Contains("""<a id="sign-in" href="/login">""", body, StringComparison.Ordinal);
        Assert.Contains("location.pathname + location.search", body, StringComparison.Ordinal);
        Assert.DoesNotContain("location.href", body, StringComparison.Ordinal);

        // The name has to be the one the login page already reads; two spellings would silently
        // send everybody home.
        Assert.Contains("'/login?returnUrl=' + encodeURIComponent(target)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_return_url_shaped_like_the_scripts_output_is_honoured()
    {
        await SeedAccountAsync(_app);
        var client = _app.CreateHttpClient();

        // Exactly what encodeURIComponent(location.pathname + location.search) produces for
        // /invitations?state=1.
        var response = await SubmitLoginAsync(client, "/login?returnUrl=%2Finvitations%3Fstate%3D1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        var target = location.IsAbsoluteUri ? location : new Uri(ZeroWikiAppFactory.BaseAddress, location);
        Assert.Equal(ZeroWikiAppFactory.BaseAddress.Authority, target.Authority);
        Assert.Equal("/invitations", target.AbsolutePath);
        Assert.Equal("?state=1", target.Query);
    }

    [Theory]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    public async Task A_hostile_return_url_arriving_the_same_way_is_still_rejected(string encoded)
    {
        // location.pathname is "//evil.example" on https://localhost//evil.example, so the script
        // can be made to write one of these. The script is a convenience; LocalUrl on the login
        // page is the boundary, and this is it holding against the value's new route in.
        await SeedAccountAsync(_app);

        var response = await SubmitLoginAsync(_app.CreateHttpClient(), $"/login?returnUrl={encoded}");

        HttpAssertions.AssertRedirectedTo("/", response);
    }

    [Fact]
    public async Task The_authorization_fallback_policy_denies_anonymous_users()
    {
        // Asserted as a condition rather than through its effect (AD19), because the gate answers
        // first and this policy is deliberately unobservable while the gate works. It is what still
        // refuses to serve a matched endpoint's content if the gate is ever removed.
        await _app.CreateHttpClient().GetAsync("/");

        var options = _app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(
            options.FallbackPolicy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>Every header of a response bar the one that legitimately differs between two.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ComparableHeaders(HttpResponseMessage response) =>
    [
        .. response.Headers.Concat(response.Content.Headers)
            .Where(header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .Select(header => KeyValuePair.Create(header.Key, string.Join(", ", header.Value)))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase),
    ];

    private static async Task SeedAccountAsync(ZeroWikiAppFactory app) =>
        await app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = Username,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = Username,
                CreatedAt = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

    private static async Task<HttpResponseMessage> SubmitLoginAsync(HttpClient client, string url)
    {
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, url);

        return await StaticSsrForm.PostAsync(client, url, fields.Concat(
        [
            KeyValuePair.Create("Input.Username", Username),
            KeyValuePair.Create("Input.Password", Password),
        ]));
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
                Username = Username,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = Username,
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

    private async Task<HttpClient> SignInAsync()
    {
        var client = _app.CreateHttpClient();

        HttpAssertions.AssertRedirectedTo("/", await SubmitLoginAsync(client, "/login"));

        return client;
    }

    /// <remarks>
    /// Matches the whole opening tag rather than an <c>href="…"</c>, because Blazor renders
    /// <c>href=""</c> as a bare <c>href</c> attribute: a pattern requiring the quotes silently
    /// skips the site brand, which is how an earlier version of this file counted one anchor on a
    /// page carrying two.
    /// </remarks>
    [GeneratedRegex("""<a\b[^>]*>""")]
    private static partial Regex AnchorTag();

    /// <summary>Reads an anchor's href, which may legitimately have no value.</summary>
    /// <remarks>
    /// The value group is optional because Blazor renders <c>href=""</c> with no value and no
    /// quotes at all — <c>&lt;a class="navbar-brand" href b-4dirb8zo57&gt;</c> — so a bare
    /// attribute yields the empty string rather than failing to match. The leading <c>\s</c>
    /// rather than <c>\b</c> keeps it from matching the tail of <c>data-href</c>.
    /// </remarks>
    [GeneratedRegex("""\shref(?:="(?<href>[^"]*)")?""")]
    private static partial Regex HrefAttribute();
}
