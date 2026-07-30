using System.Net;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises the bootstrap page over HTTP against the real application, which is the only level
/// at which "the form actually works" can be observed.
/// </summary>
public sealed class BootstrapPageTests : IDisposable
{
    private const string Username = "admin";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Submitting_the_form_creates_the_administrator_and_closes_the_path()
    {
        var client = _app.CreateHttpClient();

        var page = await client.GetAsync("/bootstrap");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The names the form actually renders — not names restated here, which would let the
        // rendered form and its binder drift apart without any test noticing.
        var fields = await StaticSsrForm.GetFieldNamesAsync(client, "/bootstrap");
        Assert.Contains("Input.Username", fields);
        Assert.Contains("Input.Password", fields);
        Assert.Contains("Input.ConfirmPassword", fields);

        var response = await SubmitAsync(client, Username, Password, Password);

        AssertRedirectedTo("/bootstrap/complete", response);

        var account = Assert.Single(await _app.GetAccountsAsync());
        Assert.Equal(Username, account.Username);
        Assert.True(account.IsAdministrator);
        Assert.True(new Argon2idPasswordHasher().Verify(Password, account.PasswordHash));

        // Inert immediately, on the same running application.
        AssertRedirectedTo("/", await client.GetAsync("/bootstrap"));
    }

    [Fact]
    public async Task Submitting_without_an_antiforgery_token_is_rejected()
    {
        var client = _app.CreateHttpClient();

        var hidden = await StaticSsrForm.GetHiddenFieldsAsync(client, "/bootstrap");
        hidden.Remove("__RequestVerificationToken");

        var response = await StaticSsrForm.PostAsync(client, "/bootstrap", hidden.Concat(
        [
            KeyValuePair.Create("Input.Username", Username),
            KeyValuePair.Create("Input.Password", Password),
            KeyValuePair.Create("Input.ConfirmPassword", Password),
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _app.GetAccountsAsync());
    }

    [Fact]
    public async Task A_password_below_the_minimum_length_is_rejected_and_creates_nothing()
    {
        var client = _app.CreateHttpClient();

        var response = await SubmitAsync(client, Username, "short", "short");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            CredentialPolicy.MinimumPasswordLengthRuleDescription,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Empty(await _app.GetAccountsAsync());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("___")]
    [InlineData("café")]
    [InlineData("admin\tx")]
    public async Task A_username_outside_the_permitted_charset_is_rejected(string username)
    {
        var client = _app.CreateHttpClient();

        var response = await SubmitAsync(client, username, Password, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Asserting the message, not just the status: a form that posts nothing also returns 200
        // with no account, so a status-only assertion passes against a completely dead form.
        Assert.Contains(
            CredentialPolicy.UsernameRuleDescription,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Empty(await _app.GetAccountsAsync());
    }

    [Theory]
    [InlineData("  admin  ")]
    [InlineData("admin\n")]
    public async Task A_username_with_surrounding_whitespace_is_accepted_and_trimmed(string username)
    {
        // Surrounding whitespace — including the newline a paste can carry — is what the form
        // trims before validating, so it must not come back with a message about the character
        // set. The pattern itself still rejects both untrimmed; see CredentialPolicyTests.
        var client = _app.CreateHttpClient();

        AssertRedirectedTo("/bootstrap/complete", await SubmitAsync(client, username, Password, Password));

        Assert.Equal("admin", Assert.Single(await _app.GetAccountsAsync()).Username);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("a.b-c_1")]
    [InlineData("_x_")]
    public async Task A_username_within_the_permitted_charset_is_accepted(string username)
    {
        var client = _app.CreateHttpClient();

        AssertRedirectedTo("/bootstrap/complete", await SubmitAsync(client, username, Password, Password));

        Assert.Equal(username, Assert.Single(await _app.GetAccountsAsync()).Username);
    }

    [Fact]
    public async Task The_completion_page_does_not_claim_an_administrator_exists_before_one_does()
    {
        var client = _app.CreateHttpClient();

        AssertRedirectedTo("/bootstrap", await client.GetAsync("/bootstrap/complete"));

        await SubmitAsync(client, Username, Password, Password);

        var afterwards = await client.GetAsync("/bootstrap/complete");
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
        Assert.Contains(
            "Administrator account created",
            await afterwards.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private static void AssertRedirectedTo(string expectedPath, HttpResponseMessage response) =>
        HttpAssertions.AssertRedirectedTo(expectedPath, response);

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string username,
        string password,
        string confirmPassword)
    {
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/bootstrap");

        return await StaticSsrForm.PostAsync(client, "/bootstrap", fields.Concat(
        [
            KeyValuePair.Create("Input.Username", username),
            KeyValuePair.Create("Input.Password", password),
            KeyValuePair.Create("Input.ConfirmPassword", confirmPassword),
        ]));
    }
}
