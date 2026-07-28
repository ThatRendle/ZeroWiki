using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises login and logout over HTTP against the real application: the form, the cookie, the
/// antiforgery token and the redirect are all only observable at this level.
/// </summary>
public sealed partial class LoginPageTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Correct_credentials_establish_a_session()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();

        var fields = await StaticSsrForm.GetFieldNamesAsync(client, "/login");
        Assert.Contains("Input.Username", fields);
        Assert.Contains("Input.Password", fields);

        var response = await SubmitAsync(client, "/login", Username, Password);

        AssertRedirectedTo("/", response);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("ZeroWiki.Authentication=", StringComparison.Ordinal));

        // Observable proof the session is live on a later request.
        Assert.Contains(
            $"You are signed in as <strong>{Username}</strong>",
            await client.GetStringAsync("/logout"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_cookie_is_http_only_secure_and_same_site_lax()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();

        var response = await SubmitAsync(client, "/login", Username, Password);

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("ZeroWiki.Authentication=", StringComparison.Ordinal));

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_username_and_a_known_one_are_indistinguishable()
    {
        // The comparison that matters: the *same* submitted username against a store where it
        // exists and one where it does not. Comparing two different usernames would prove
        // nothing, because the form legitimately echoes back whatever was typed.
        var hasher = new Argon2idPasswordHasher();

        using var withoutAccount = new ZeroWikiAppFactory();

        using var withAccount = new ZeroWikiAppFactory();
        await SeedAccountAsync(withAccount, Username, hasher.Hash(Password));

        using var withCorruptHash = new ZeroWikiAppFactory();
        await SeedAccountAsync(withCorruptHash, Username, "not-a-hash");

        var rejections = new List<(HttpStatusCode Status, string Body, string[] Cookies)>();
        foreach (var app in new[] { withoutAccount, withAccount, withCorruptHash })
        {
            var response = await SubmitAsync(app.CreateHttpClient(), "/login", Username, "the wrong passphrase");

            rejections.Add((
                response.StatusCode,
                Normalise(await response.Content.ReadAsStringAsync()),
                response.Headers.TryGetValues("Set-Cookie", out var cookies) ? [.. cookies] : []));
        }

        var first = rejections[0];
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Contains("Your username or password is incorrect.", first.Body, StringComparison.Ordinal);

        foreach (var rejection in rejections)
        {
            Assert.Equal(first.Status, rejection.Status);
            Assert.Equal(first.Body, rejection.Body);
            Assert.DoesNotContain(
                rejection.Cookies,
                cookie => cookie.StartsWith("ZeroWiki.Authentication=", StringComparison.Ordinal));
        }

        Assert.Empty(await withoutAccount.GetAccountsAsync());
    }

    [Fact]
    public async Task A_rejection_never_says_which_part_was_wrong()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));

        var body = await (await SubmitAsync(
            _app.CreateHttpClient(),
            "/login",
            Username,
            "the wrong passphrase")).Content.ReadAsStringAsync();

        Assert.Contains("Your username or password is incorrect.", body, StringComparison.Ordinal);
        foreach (var leak in new[] { "no such", "unknown user", "does not exist", "wrong password", "unusable" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Signing_out_leaves_later_requests_unauthenticated()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();
        await SubmitAsync(client, "/login", Username, Password);

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/logout");
        var response = await StaticSsrForm.PostAsync(client, "/logout", fields);

        AssertRedirectedTo("/", response);

        // The landing page is served to unauthenticated requests and to nothing else (AD21), so
        // getting it back from a page that needs a session is the observable form of "no longer
        // authenticated". Before AD21 this asserted the signed-out branch of /logout, which the
        // gate now answers before the component is ever reached.
        await HttpAssertions.AssertIsAnonymousLandingPageAsync(await client.GetAsync("/logout"));
    }

    [Fact]
    public async Task Signing_out_requires_a_post_with_an_antiforgery_token()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();
        await SubmitAsync(client, "/login", Username, Password);

        // A GET must not sign anyone out — otherwise any page that can make the browser fetch
        // this URL, an <img> included, logs the user out.
        var viaGet = await client.GetAsync("/logout");
        Assert.Equal(HttpStatusCode.OK, viaGet.StatusCode);
        Assert.Contains("You are signed in as", await viaGet.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/logout");
        fields.Remove("__RequestVerificationToken");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, "/logout", fields)).StatusCode);

        // Still signed in after both attempts.
        Assert.Contains("You are signed in as", await client.GetStringAsync("/logout"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surrounding_whitespace_in_the_username_is_trimmed_before_the_lookup()
    {
        // Matches every path that writes a username; a pasted trailing space must not look like
        // a wrong credential.
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();

        AssertRedirectedTo("/", await SubmitAsync(client, "/login", $"  {Username}  ", Password));
    }

    [Fact]
    public async Task A_local_return_url_is_honoured()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();

        var response = await SubmitAsync(client, "/login?returnUrl=%2Fbootstrap%2Fcomplete", Username, Password);

        AssertRedirectedTo("/bootstrap/complete", response);
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.example%2Fphish")]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    [InlineData("evil.example")]
    [InlineData("%20%2F%2Fevil.example")]
    public async Task An_off_site_return_url_is_ignored(string encodedReturnUrl)
    {
        // An open redirect on a login page is a phishing primitive: the victim authenticates
        // against the genuine form and is then handed somewhere else.
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        var client = _app.CreateHttpClient();

        var response = await SubmitAsync(client, $"/login?returnUrl={encodedReturnUrl}", Username, Password);

        AssertRedirectedTo("/", response);
    }

    [Fact]
    public async Task A_corrupt_timestamp_row_still_gets_the_uniform_rejection_rather_than_an_error()
    {
        await SeedAccountAsync(_app, Username, new Argon2idPasswordHasher().Hash(Password));
        await _app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE Accounts SET CreatedAt = 'not-a-timestamp'"));

        var client = _app.CreateHttpClient();

        var rejected = await SubmitAsync(client, "/login", Username, "the wrong passphrase");
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains(
            "Your username or password is incorrect.",
            await rejected.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // And the account is still usable — the projection never reads the broken column.
        AssertRedirectedTo("/", await SubmitAsync(client, "/login", Username, Password));
    }

    private static async Task SeedAccountAsync(ZeroWikiAppFactory app, string username, string passwordHash) =>
        await app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = passwordHash,
                DisplayName = username,
                CreatedAt = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string url,
        string username,
        string password)
    {
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, url);

        return await StaticSsrForm.PostAsync(client, url, fields.Concat(
        [
            KeyValuePair.Create("Input.Username", username),
            KeyValuePair.Create("Input.Password", password),
        ]));
    }

    /// <summary>
    /// Blanks the antiforgery token, which is regenerated per response and would otherwise make
    /// two identical pages compare unequal.
    /// </summary>
    private static string Normalise(string html) => AntiforgeryToken().Replace(html, "TOKEN");

    private static void AssertRedirectedTo(string expectedPath, HttpResponseMessage response) =>
        HttpAssertions.AssertRedirectedTo(expectedPath, response);

    [GeneratedRegex("""name="__RequestVerificationToken" value="[^"]*""")]
    private static partial Regex AntiforgeryToken();
}
