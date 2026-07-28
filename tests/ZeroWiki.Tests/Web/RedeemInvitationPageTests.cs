using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Walks the whole invitation journey over HTTP against the real application: a member issues a
/// link, an anonymous invitee opens it, chooses credentials, and signs in with them.
/// </summary>
/// <remarks>
/// The invitee is always a <em>separate</em> client with no cookie of its own — the point of the
/// block is that this half of §4 is anonymous, and a test that reused the issuer's client would be
/// exercising an authenticated request while claiming otherwise. The link is scraped from the page
/// that issued it rather than composed here, which also makes this the first test that proves 4a's
/// rendered link actually resolves.
/// </remarks>
public sealed partial class RedeemInvitationPageTests : IDisposable
{
    private const string IssuerPassword = "a good long passphrase";
    private const string InviteePassword = "another good long passphrase";
    private const string Issuer = "alice";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_invitee_redeems_the_link_and_is_sent_to_sign_in()
    {
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        var page = await invitee.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The names the form actually renders, not names restated here — a form whose rendered
        // names have drifted from its binder posts nothing and still returns 200.
        var fields = await StaticSsrForm.GetFieldNamesAsync(invitee, link);
        Assert.Contains("Input.Username", fields);
        Assert.Contains("Input.Password", fields);
        Assert.Contains("Input.ConfirmPassword", fields);

        var response = await SubmitAsync(invitee, link, "bob", InviteePassword);

        // AD18: the account is created, the session is not. Login stays the only route that mints
        // one, so the invitee lands on the sign-in page.
        HttpAssertions.AssertRedirectedTo("/login", response);

        var account = Assert.Single(await _app.GetAccountsAsync(), a => a.Username == "bob");
        Assert.Equal("bob", account.DisplayName);
        Assert.False(account.IsAdministrator);

        // Hashed with the real Argon2id through DI, and the plaintext is nowhere in the row.
        Assert.True(new Argon2idPasswordHasher().Verify(InviteePassword, account.PasswordHash));
        Assert.DoesNotContain(InviteePassword, account.PasswordHash, StringComparison.Ordinal);

        Assert.NotNull(Assert.Single(await GetInvitationsAsync()).RedeemedAt);
    }

    [Fact]
    public async Task The_redeemed_account_can_sign_in_with_the_credentials_it_chose()
    {
        // The end of the requirement, and the only assertion that proves the two paths agree about
        // what a stored password hash is.
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        await SubmitAsync(invitee, link, "bob", InviteePassword);

        HttpAssertions.AssertRedirectedTo("/", await SignInAsync(_app.CreateHttpClient(), "bob", InviteePassword));
    }

    [Fact]
    public async Task Redeeming_does_not_sign_the_invitee_in()
    {
        // AD18 stated as the property that matters: the invitee's client holds no session after a
        // successful redemption, so a page requiring one still bounces it to login.
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        var response = await SubmitAsync(invitee, link, "bob", InviteePassword);

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var headers) ? headers : [];
        Assert.DoesNotContain(setCookies, header => header.StartsWith("ZeroWiki.Authentication=", StringComparison.Ordinal));

        // A page requiring a session answers an unauthenticated caller with the landing page and
        // nothing else (AD21), so getting it back is the observable form of "holds no session".
        await HttpAssertions.AssertIsAnonymousLandingPageAsync(await invitee.GetAsync("/invitations"));
    }

    [Fact]
    public async Task The_consumed_token_is_not_carried_into_the_redirect()
    {
        // A redemption link in a URL lands in browser history, server logs and every proxy in
        // between. Redemption consuming it is what bounds that exposure; putting it in the next
        // URL would unbound it again.
        var link = await IssueInvitationAsync();

        var response = await SubmitAsync(_app.CreateHttpClient(), link, "bob", InviteePassword);

        Assert.DoesNotContain(
            TokenOf(link),
            Assert.IsType<Uri>(response.Headers.Location).ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_matching_no_invitation_offers_no_form_and_creates_nothing()
    {
        await IssueInvitationAsync();

        var body = await _app.CreateHttpClient().GetStringAsync($"{InvitationPolicy.RedemptionPath}/{NewToken()}");

        Assert.Contains("is not valid", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Password", body, StringComparison.Ordinal);
        await AssertOnlyTheIssuerExistsAsync();
    }

    [Fact]
    public async Task An_unknown_token_is_indistinguishable_from_a_malformed_one()
    {
        // AD17's boundary from the outside: nothing an anonymous caller can supply without holding
        // a real token produces a different answer.
        await IssueInvitationAsync();
        var client = _app.CreateHttpClient();

        var unknown = await client.GetStringAsync($"{InvitationPolicy.RedemptionPath}/{NewToken()}");
        var malformed = await client.GetStringAsync($"{InvitationPolicy.RedemptionPath}/not-a-token");

        Assert.Equal(unknown, malformed);
    }

    [Fact]
    public async Task An_unmatched_token_reveals_nothing_about_whether_the_username_exists()
    {
        // The page-level twin of the service assertion. Submitting a name that exists and one that
        // does not, both with a token matching nothing, must be byte-identical — otherwise the
        // uniqueness check has become an enumeration oracle reachable by an anonymous stranger with
        // no invitation at all, which is what UsernameTaken's <remarks> says it is not.
        var link = await IssueInvitationAsync();
        var dead = $"{InvitationPolicy.RedemptionPath}/{NewToken()}";
        var invitee = _app.CreateHttpClient();

        // Fields come from the live form, since a dead link renders none — the same shape as a
        // submission made just after the link stopped working.
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(invitee, link);

        var existing = await PostAsync(invitee, dead, fields, Issuer, InviteePassword);
        var unknown = await PostAsync(invitee, dead, fields, "nobody-at-all", InviteePassword);

        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await existing.Content.ReadAsStringAsync());

        await AssertOnlyTheIssuerExistsAsync();
    }

    [Theory]
    [InlineData(InvitationState.Expired, "has expired")]
    [InlineData(InvitationState.Revoked, "was withdrawn")]
    [InlineData(InvitationState.Redeemed, "has already been used")]
    public async Task An_invitation_that_goes_bad_while_the_form_is_open_is_refused_on_the_post(
        InvitationState state,
        string expectedMessage)
    {
        // The realistic shape of 4.3: the invitee opened a live link and the invitation went bad
        // before they submitted. It also proves the POST re-decides rather than trusting the GET —
        // a hand-crafted POST never does the GET at all.
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(invitee, link);
        await MoveInvitationToAsync(state);

        var response = await PostAsync(invitee, link, fields, "bob", InviteePassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertNoAccountNamedAsync("bob");
    }

    [Theory]
    [InlineData(InvitationState.Expired, "has expired")]
    [InlineData(InvitationState.Revoked, "was withdrawn")]
    [InlineData(InvitationState.Redeemed, "has already been used")]
    public async Task An_invitation_that_is_already_bad_shows_the_reason_and_no_form(
        InvitationState state,
        string expectedMessage)
    {
        var link = await IssueInvitationAsync();
        await MoveInvitationToAsync(state);

        var body = await _app.CreateHttpClient().GetStringAsync(link);

        Assert.Contains(expectedMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reusing_a_link_that_already_created_an_account_creates_no_second_one()
    {
        var link = await IssueInvitationAsync();

        HttpAssertions.AssertRedirectedTo(
            "/login",
            await SubmitAsync(_app.CreateHttpClient(), link, "bob", InviteePassword));

        var second = await _app.CreateHttpClient().GetStringAsync(link);
        Assert.Contains("has already been used", second, StringComparison.Ordinal);

        Assert.Equal(2, (await _app.GetAccountsAsync()).Count);
    }

    [Fact]
    public async Task Redeeming_requires_a_post_carrying_an_antiforgery_token()
    {
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        // An account-creating action reachable by GET is triggerable by any page that can make the
        // browser fetch a URL, and an <img> tag is enough.
        var viaGet = await invitee.GetAsync(
            $"{link}?_handler=redeem-invitation&Input.Username=bob&Input.Password={InviteePassword}"
            + $"&Input.ConfirmPassword={InviteePassword}");
        Assert.Equal(HttpStatusCode.OK, viaGet.StatusCode);
        await AssertNoAccountNamedAsync("bob");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(invitee, link);
        fields.Remove("__RequestVerificationToken");

        var response = await PostAsync(invitee, link, fields, "bob", InviteePassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoAccountNamedAsync("bob");
    }

    [Fact]
    public async Task A_password_below_the_minimum_length_is_rejected_and_creates_nothing()
    {
        // AD10, the second of the two paths where somebody chooses their own password. The message
        // is asserted, not just the status: a form that posts nothing also returns 200 with no
        // account, so a status-only assertion passes against a completely dead form.
        var link = await IssueInvitationAsync();
        var tooShort = new string('x', CredentialPolicy.MinimumPasswordLength - 1);

        var response = await SubmitAsync(_app.CreateHttpClient(), link, "bob", tooShort);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            CredentialPolicy.MinimumPasswordLengthRuleDescription,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        await AssertNoAccountNamedAsync("bob");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("___")]
    public async Task A_username_outside_the_permitted_charset_is_rejected_and_creates_nothing(string username)
    {
        var link = await IssueInvitationAsync();

        var response = await SubmitAsync(_app.CreateHttpClient(), link, username, InviteePassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            CredentialPolicy.UsernameRuleDescription,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        await AssertOnlyTheIssuerExistsAsync();
    }

    [Fact]
    public async Task Mismatched_passwords_are_rejected_and_create_nothing()
    {
        var link = await IssueInvitationAsync();

        var response = await SubmitAsync(
            _app.CreateHttpClient(),
            link,
            "bob",
            InviteePassword,
            $"{InviteePassword}x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertNoAccountNamedAsync("bob");
    }

    [Fact]
    public async Task A_taken_username_keeps_the_form_up_and_the_invitation_usable()
    {
        // A name clash says nothing about the invitation, so it must not burn a link that is still
        // perfectly good — the invitee picks another name and carries on with the same one.
        var link = await IssueInvitationAsync();
        var invitee = _app.CreateHttpClient();

        var clash = await SubmitAsync(invitee, link, Issuer, InviteePassword);

        Assert.Equal(HttpStatusCode.OK, clash.StatusCode);
        Assert.Contains(
            "already taken",
            await clash.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Null(Assert.Single(await GetInvitationsAsync()).RedeemedAt);

        HttpAssertions.AssertRedirectedTo("/login", await SubmitAsync(invitee, link, "bob", InviteePassword));
        Assert.NotNull(Assert.Single(await GetInvitationsAsync()).RedeemedAt);
    }

    /// <summary>Which way an invitation is pushed out of use before a redemption is attempted.</summary>
    public enum InvitationState
    {
        Expired,
        Revoked,
        Redeemed,
    }

    private static string TokenOf(string link) =>
        Uri.UnescapeDataString(link[(link.IndexOf($"{InvitationPolicy.RedemptionPath}/", StringComparison.Ordinal)
            + InvitationPolicy.RedemptionPath.Length + 1)..]);

    private static string NewToken() => new SecretTokenGenerator().Generate().Plaintext;

    /// <summary>
    /// Signs a member in, issues an invitation through the real page, and returns the link that
    /// page rendered — path and token exactly as a browser would have copied them.
    /// </summary>
    private async Task<string> IssueInvitationAsync()
    {
        await SeedAccountAsync(Issuer);

        var issuer = _app.CreateHttpClient();
        HttpAssertions.AssertRedirectedTo("/", await SignInAsync(issuer, Issuer, IssuerPassword));

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(issuer, "/invitations", "issue-invitation");
        var issued = await StaticSsrForm.PostAsync(issuer, "/invitations", fields);

        var body = await issued.Content.ReadAsStringAsync();
        var match = InvitationLink().Match(body);
        Assert.True(match.Success, $"The invitations page rendered no link to redeem.\n{body}");

        return new Uri(WebUtility.HtmlDecode(match.Groups["url"].Value), UriKind.Absolute).PathAndQuery;
    }

    private async Task MoveInvitationToAsync(InvitationState state) =>
        await _app.WithDbAsync(async db =>
        {
            var invitation = await db.Invitations.SingleAsync();
            var now = DateTimeOffset.UtcNow;

            switch (state)
            {
                case InvitationState.Expired:
                    invitation.ExpiresAt = now - TimeSpan.FromMinutes(1);
                    break;
                case InvitationState.Revoked:
                    invitation.RevokedAt = now;
                    break;
                case InvitationState.Redeemed:
                    invitation.RedeemedAt = now;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }

            await db.SaveChangesAsync();
        });

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string link,
        string username,
        string password,
        string? confirmPassword = null) =>
        await PostAsync(
            client,
            link,
            await StaticSsrForm.GetHiddenFieldsAsync(client, link),
            username,
            password,
            confirmPassword);

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string link,
        Dictionary<string, string> fields,
        string username,
        string password,
        string? confirmPassword = null) =>
        await StaticSsrForm.PostAsync(client, link, fields.Concat(
        [
            KeyValuePair.Create("Input.Username", username),
            KeyValuePair.Create("Input.Password", password),
            KeyValuePair.Create("Input.ConfirmPassword", confirmPassword ?? password),
        ]));

    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string username,
        string password)
    {
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/login");

        return await StaticSsrForm.PostAsync(client, "/login", fields.Concat(
        [
            KeyValuePair.Create("Input.Username", username),
            KeyValuePair.Create("Input.Password", password),
        ]));
    }

    private async Task SeedAccountAsync(string username) =>
        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = new Argon2idPasswordHasher().Hash(IssuerPassword),
                DisplayName = username,
                CreatedAt = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

    private async Task<IReadOnlyList<Invitation>> GetInvitationsAsync() =>
        await _app.WithDbAsync(db => db.Invitations.AsNoTracking().ToListAsync());

    private async Task AssertNoAccountNamedAsync(string username) =>
        Assert.DoesNotContain(await _app.GetAccountsAsync(), a => a.Username == username);

    private async Task AssertOnlyTheIssuerExistsAsync() =>
        Assert.Equal(Issuer, Assert.Single(await _app.GetAccountsAsync()).Username);

    [GeneratedRegex("""class="invitation-link">(?<url>[^<]+)<""")]
    private static partial Regex InvitationLink();
}
