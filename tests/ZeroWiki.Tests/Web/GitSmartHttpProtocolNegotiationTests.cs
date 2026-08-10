using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// §7 remediation, supervisor blocker 2: <c>Git-Protocol</c> was never read off the request at all, so
/// every clone/fetch/push silently ran protocol v0 — invisible to every prior test, since every real git
/// client falls back to v0 transparently. Exercised over real HTTP through the actual endpoint wiring
/// (<see cref="ZeroWiki.Web.GitSmartHttpEndpoints.InvokeGitHttpBackendAsync"/>'s header read), not just
/// <see cref="ZeroWiki.Content.GitHttpBackendHost"/> directly — <c>GitHttpBackendHostTests</c> covers the
/// host's own forwarding; this file covers the header actually being read off a real request.
/// </summary>
public sealed class GitSmartHttpProtocolNegotiationTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase for alice";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_client_that_sends_Git_Protocol_version_2_is_answered_with_a_v2_advertisement()
    {
        var token = await SeedAccountWithTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/git/info/refs?service=git-upload-pack");
        request.Headers.Add("Git-Protocol", "version=2");
        using var response = await AuthenticatedClient(token).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        var v2Prefix = "000eversion 2\n"u8.ToArray();
        Assert.Equal(v2Prefix, body.Take(v2Prefix.Length));
    }

    [Fact]
    public async Task A_client_that_sends_no_Git_Protocol_header_still_gets_the_ordinary_v0_advertisement()
    {
        var token = await SeedAccountWithTokenAsync();

        using var response = await AuthenticatedClient(token)
            .GetAsync("/git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        var v0Prefix = "001e# service=git-upload-pack\n"u8.ToArray();
        Assert.Equal(v0Prefix, body.Take(v0Prefix.Length));
    }

    private HttpClient AuthenticatedClient((Guid Id, string Plaintext) token)
    {
        var client = _app.CreateHttpClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{token.Plaintext}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    private async Task<(Guid Id, string Plaintext)> SeedAccountWithTokenAsync()
    {
        var hasher = new Argon2idPasswordHasher();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Username = Username,
            PasswordHash = hasher.Hash(Password),
            DisplayName = Username,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        });

        var generator = new SecretTokenGenerator();
        var secret = generator.Generate();
        var token = new GitToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
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
}
