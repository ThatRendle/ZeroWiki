using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises the invitations page over HTTP against the real application. The authorization
/// middleware, the antiforgery token, the form binding and the once-only rendering of the
/// redemption link are only observable at this level.
/// </summary>
public sealed partial class InvitationsPageTests : IDisposable
{
    private const string Password = "a good long passphrase";
    private const string Page = "/invitations";
    private const string IssueForm = "issue-invitation";
    private const string RevokeForm = "revoke-invitation";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly SecretTokenGenerator _tokenGenerator = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_login_instead_of_the_page()
    {
        // "As an authenticated member" is half of the requirement, and without this test it is one
        // forgotten attribute away from being false.
        var response = await _app.CreateHttpClient().GetAsync(Page);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = Assert.IsType<Uri>(response.Headers.Location);
        var target = location.IsAbsoluteUri ? location : new Uri(ZeroWikiAppFactory.BaseAddress, location);
        Assert.Equal(ZeroWikiAppFactory.BaseAddress.Authority, target.Authority);
        Assert.Equal("/login", target.AbsolutePath);
        Assert.Contains(Uri.EscapeDataString(Page), target.Query, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "Create an invitation",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_post_cannot_issue_an_invitation()
    {
        await SeedAccountAsync("alice");

        var response = await StaticSsrForm.PostAsync(
            _app.CreateHttpClient(),
            Page,
            [KeyValuePair.Create("_handler", IssueForm)]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await GetInvitationsAsync());
    }

    [Fact]
    public async Task A_member_issues_an_invitation_and_the_link_is_shown_exactly_once()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await SubmitAsync(client, IssueForm);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var link = InvitationLink().Match(body);
        Assert.True(link.Success, $"The issued invitation link was not rendered.\n{body}");

        var url = new Uri(WebUtility.HtmlDecode(link.Groups["url"].Value), UriKind.Absolute);
        Assert.Equal(ZeroWikiAppFactory.BaseAddress.Authority, url.Authority);
        Assert.StartsWith(
            $"{InvitationPolicy.RedemptionPath}/",
            url.AbsolutePath,
            StringComparison.Ordinal);

        // The plaintext in the link is the secret the stored hash was derived from...
        var token = Uri.UnescapeDataString(url.AbsolutePath[(InvitationPolicy.RedemptionPath.Length + 1)..]);
        var stored = Assert.Single(await GetInvitationsAsync());
        Assert.Equal(_tokenGenerator.ComputeHash(token), stored.TokenHash);

        // ...and it exists nowhere in the store, in any column.
        Assert.DoesNotContain(token, await DumpInvitationRowsAsync(), StringComparison.Ordinal);

        // Shown once: it is rendered by the response that created it and never again.
        Assert.DoesNotContain(token, await client.GetStringAsync(Page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_issued_invitation_expires_the_policy_lifetime_after_it_is_issued()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        await SubmitAsync(client, IssueForm);

        var stored = Assert.Single(await GetInvitationsAsync());
        Assert.Equal(stored.CreatedAt + InvitationPolicy.Lifetime, stored.ExpiresAt);

        // And the page tells the issuer the same number the store was written with.
        Assert.Contains(
            InvitationPolicy.LifetimeRuleDescription,
            await client.GetStringAsync(Page),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_does_not_see_another_members_invitation()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        await SubmitAsync(alice, IssueForm);
        var aliceInvitation = Assert.Single(await GetInvitationsAsync());

        var bob = await SignInAsync("bob");
        var body = await bob.GetStringAsync(Page);

        Assert.DoesNotContain(aliceInvitation.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No invitations yet.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_sees_another_members_invitation()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("root", isAdministrator: true);

        var alice = await SignInAsync("alice");
        await SubmitAsync(alice, IssueForm);
        var aliceInvitation = Assert.Single(await GetInvitationsAsync());

        var root = await SignInAsync("root");
        var body = await root.GetStringAsync(Page);

        Assert.Contains(aliceInvitation.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_revokes_their_own_invitation()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await SubmitAsync(client, IssueForm);

        var invitation = Assert.Single(await GetInvitationsAsync());
        var response = await SubmitAsync(client, RevokeForm, ("RevokeInput.InvitationId", invitation.Id.ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await GetInvitationsAsync()).Single().RevokedAt);
    }

    [Fact]
    public async Task A_member_cannot_revoke_another_members_invitation_by_posting_its_identifier()
    {
        // The identifier is attacker-supplied, so the page must not be what decides this. AD15 puts
        // the check in the service precisely so a route that forgets it cannot reach past it.
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        await SubmitAsync(alice, IssueForm);
        var aliceInvitation = Assert.Single(await GetInvitationsAsync());

        var bob = await SignInAsync("bob");
        var response = await SubmitAsync(bob, RevokeForm, ("RevokeInput.InvitationId", aliceInvitation.Id.ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await GetInvitationsAsync()).Single().RevokedAt);
    }

    [Fact]
    public async Task An_administrator_can_revoke_another_members_invitation()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("root", isAdministrator: true);

        var alice = await SignInAsync("alice");
        await SubmitAsync(alice, IssueForm);
        var aliceInvitation = Assert.Single(await GetInvitationsAsync());

        var root = await SignInAsync("root");
        await SubmitAsync(root, RevokeForm, ("RevokeInput.InvitationId", aliceInvitation.Id.ToString()));

        Assert.NotNull((await GetInvitationsAsync()).Single().RevokedAt);
    }

    [Fact]
    public async Task Revoking_requires_a_post_carrying_an_antiforgery_token()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await SubmitAsync(client, IssueForm);
        var invitation = Assert.Single(await GetInvitationsAsync());

        // A revoke reachable by GET is triggerable by any page that can make the browser fetch a
        // URL, and an <img> tag is enough.
        var viaGet = await client.GetAsync(
            $"{Page}?_handler={RevokeForm}&RevokeInput.InvitationId={invitation.Id}");
        Assert.Equal(HttpStatusCode.OK, viaGet.StatusCode);
        Assert.Null((await GetInvitationsAsync()).Single().RevokedAt);

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, RevokeForm);
        fields.Remove("__RequestVerificationToken");
        fields["RevokeInput.InvitationId"] = invitation.Id.ToString();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, Page, fields)).StatusCode);
        Assert.Null((await GetInvitationsAsync()).Single().RevokedAt);
    }

    [Fact]
    public async Task Issuing_requires_a_post()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync($"{Page}?_handler={IssueForm}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetInvitationsAsync());
    }

    /// <summary>Posts one of the page's forms, carrying the hidden fields it renders.</summary>
    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string formName,
        params (string Name, string Value)[] extraFields)
    {
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, formName);

        foreach (var (name, value) in extraFields)
        {
            fields[name] = value;
        }

        return await StaticSsrForm.PostAsync(client, Page, fields);
    }

    private async Task SeedAccountAsync(string username, bool isAdministrator = false) =>
        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = username,
                IsAdministrator = isAdministrator,
                CreatedAt = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

    private async Task<HttpClient> SignInAsync(string username)
    {
        var client = _app.CreateHttpClient();
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/login");

        var response = await StaticSsrForm.PostAsync(client, "/login", fields.Concat(
        [
            KeyValuePair.Create("Input.Username", username),
            KeyValuePair.Create("Input.Password", Password),
        ]));

        HttpAssertions.AssertRedirectedTo("/", response);

        return client;
    }

    private async Task<IReadOnlyList<Invitation>> GetInvitationsAsync() =>
        await _app.WithDbAsync(db => db.Invitations.AsNoTracking().ToListAsync());

    /// <summary>
    /// Every column of every invitation row, so "the plaintext is not stored" is asserted against
    /// the whole row rather than against the one column it was least likely to be in.
    /// </summary>
    private async Task<string> DumpInvitationRowsAsync() =>
        string.Join('\n', await _app.WithDbAsync(async db =>
        {
            var rows = new List<string>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id || '|' || TokenHash || '|' || IssuerAccountId || '|' || CreatedAt || '|' || ExpiresAt "
                + "|| '|' || COALESCE(RedeemedAt, '') || '|' || COALESCE(RevokedAt, '') FROM Invitations";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }));

    [GeneratedRegex("""class="invitation-link">(?<url>[^<]+)<""")]
    private static partial Regex InvitationLink();
}
