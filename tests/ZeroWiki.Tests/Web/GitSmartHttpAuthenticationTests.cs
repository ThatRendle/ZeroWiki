using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// D18 §4, tasks 7.1/7.2/7.5: the git Smart HTTP routes' authentication surface, driven over real HTTP
/// against the real pipeline (<see cref="ZeroWikiAppFactory"/>) — the same instrument
/// <c>AnonymousAccessTests</c> uses, because the property under test (401 vs. reaching the backend) is
/// about the whole response, not just a service call. Mutation testing applies to
/// <see cref="ZeroWiki.Web.GitBasicAuthenticationFilter"/> (block B brief); these tests are its primary
/// coverage.
/// </summary>
public sealed class GitSmartHttpAuthenticationTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase for alice";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Unauthenticated_request_is_refused_with_401_and_a_basic_challenge()
    {
        var response = await _app.CreateHttpClient().GetAsync("/git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("realm=\"ZeroWiki\"", response.Headers.WwwAuthenticate.Single().Parameter);

        // No subprocess ever ran: the response carries none of git-http-backend's own output.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("service=git-upload-pack", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_git_request_never_gets_the_anonymous_landing_page_either()
    {
        // AD21's landing page is what every OTHER anonymous surface gets; the git routes opted out of
        // that mechanism entirely (AllowAnonymous() on the whole group) in favour of a real 401 -- this
        // is the negative half of that decision, proven rather than assumed.
        var response = await _app.CreateHttpClient().GetAsync("/git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Login", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_username_is_refused_uniformly()
    {
        var response = await GetInfoRefsAsync("nobody", "whatever-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_for_a_real_account_is_refused()
    {
        await SeedAccountAsync(Username);

        var response = await GetInfoRefsAsync(Username, "not-the-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var account = await SeedAccountAsync(Username);
        var token = await IssueTokenAsync(account.Id);
        await RevokeTokenAsync(account.Id, token.Id);

        var response = await GetInfoRefsAsync(Username, token.Plaintext);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_presented_under_a_different_accounts_username_is_refused()
    {
        var alice = await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");
        var aliceToken = await IssueTokenAsync(alice.Id);

        var response = await GetInfoRefsAsync("bob", aliceToken.Plaintext);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_login_password_is_not_accepted_as_a_git_credential_over_http()
    {
        var account = await SeedAccountAsync(Username, Password);
        await IssueTokenAsync(account.Id);

        var response = await GetInfoRefsAsync(Username, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_reaches_the_backend_and_a_real_advertisement_comes_back()
    {
        var account = await SeedAccountAsync(Username);
        var token = await IssueTokenAsync(account.Id);

        var response = await GetInfoRefsAsync(Username, token.Plaintext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/x-git-upload-pack-advertisement",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsByteArrayAsync();
        var expectedPrefix = "001e# service=git-upload-pack\n"u8.ToArray();
        Assert.Equal(expectedPrefix, body.Take(expectedPrefix.Length));
    }

    [Fact]
    public async Task An_empty_credential_store_still_refuses_rather_than_erroring()
    {
        // No accounts exist at all -- GitTokenService.VerifyAsync must answer null, not throw, the same
        // way LoginService already behaves for an empty store.
        var response = await GetInfoRefsAsync(Username, "anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetInfoRefsAsync(string username, string password)
    {
        var client = _app.CreateHttpClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        return await client.GetAsync("/git/info/refs?service=git-upload-pack");
    }

    private async Task<Account> SeedAccountAsync(string username, string password = Password)
    {
        var hasher = new Argon2idPasswordHasher();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.Hash(password),
            DisplayName = username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        });

        return account;
    }

    private async Task<(Guid Id, string Plaintext)> IssueTokenAsync(Guid accountId)
    {
        var generator = new SecretTokenGenerator();
        var secret = generator.Generate();

        var token = new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TokenHash = secret.Hash,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _app.WithDbAsync(async db =>
        {
            db.GitTokens.Add(token);
            await db.SaveChangesAsync();
        });

        return (token.Id, secret.Plaintext);
    }

    private async Task RevokeTokenAsync(Guid accountId, Guid tokenId) =>
        await _app.WithDbAsync(async db =>
        {
            var token = await db.GitTokens.SingleAsync(t => t.Id == tokenId && t.AccountId == accountId);
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
}
