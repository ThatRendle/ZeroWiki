using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises the account page's git access tokens and git emails over HTTP against the real
/// application. The token property this page exists to hold — a credential rendered exactly once
/// and unrecoverable afterwards — is only observable across separate requests, which is only
/// observable here; the git-email tests share this file because both surfaces live on the same
/// page and the same signed-in-caller machinery.
/// </summary>
public sealed partial class AccountPageTests : IDisposable
{
    private const string Password = "a good long passphrase";
    private const string Page = "/account";
    private const string GenerateForm = "generate-git-token";
    private const string RevokeForm = "revoke-git-token";
    private const string AddEmailForm = "add-git-email";
    private const string RemoveEmailForm = "remove-git-email";

    /// <summary>The two things the page may say about a revoke, restated so both are pinned.</summary>
    /// <remarks>
    /// Asserting that two answers are <em>equal</em> proves only indistinguishability, and a page
    /// that cheerfully reported every revoke as done would satisfy it while telling a member their
    /// token is dead when it is not. The wording is pinned as well as the equality so the honest
    /// answer is the one being compared.
    /// </remarks>
    private const string RevokedMessage = "That token is revoked and can no longer be used for git.";

    private const string NoSuchTokenMessage = "That token no longer exists.";

    private const string EmailAddedMessage = "Added.";
    private const string EmailAlreadyOnAccountMessage = "That address is already on your account.";

    /// <summary>AD24's real reason, bounded to naming that the address is taken and never to whom.</summary>
    private const string EmailTakenMessage = "That address is already associated with another account.";

    private const string EmailMalformedMessage = "Enter a valid email address.";
    private const string EmailRemovedMessage = "That email is no longer associated with your account.";
    private const string NoSuchEmailMessage = "That email no longer exists.";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly SecretTokenGenerator _tokenGenerator = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_anonymous_visitor_gets_the_landing_page_instead_of_the_page()
    {
        // "An authenticated user … their tokens" is half the requirement, and without this it is one
        // forgotten attribute away from being false. Under AD21 the denial is the landing page, not a
        // 302, so this URL is indistinguishable from one that does not exist.
        var response = await _app.CreateHttpClient().GetAsync(Page);

        await HttpAssertions.AssertIsAnonymousLandingPageAsync(response);
        Assert.DoesNotContain(
            "Generate a git access token",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_post_cannot_generate_a_git_token()
    {
        await SeedAccountAsync("alice");

        var response = await StaticSsrForm.PostAsync(
            _app.CreateHttpClient(),
            Page,
            [KeyValuePair.Create("_handler", GenerateForm)]);

        await HttpAssertions.AssertIsAnonymousLandingPageAsync(response);
        Assert.Empty(await GetTokensAsync());
    }

    [Fact]
    public async Task A_signed_in_member_gets_the_real_page()
    {
        // AD16's failure signature: a break that denies everybody leaves the anonymous test above
        // green, because the breakage denies anonymous too. This is the assertion that would fail.
        await SeedAccountAsync("alice");

        var response = await (await SignInAsync("alice")).GetAsync(Page);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await HttpAssertions.AssertIsNotAnonymousLandingPageAsync(response);
        Assert.Contains(
            "Generate a git access token",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generated_token_is_shown_once_and_cannot_be_recovered_afterwards()
    {
        var accountId = await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await SubmitAsync(client, GenerateForm);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var rendered = GitTokenValue().Match(body);
        Assert.True(rendered.Success, $"The generated token was not rendered.\n{body}");

        var token = WebUtility.HtmlDecode(rendered.Groups["token"].Value);

        // It is the real credential and not a decorative string: the stored hash was derived from
        // this exact value, and it resolves to this account through the path the git remote uses.
        var stored = Assert.Single(await GetTokensAsync());
        Assert.Equal(_tokenGenerator.ComputeHash(token), stored.TokenHash);
        Assert.Equal(accountId, (await VerifyAsync("alice", token))?.Id);

        // Once — not once per element. A second copy in a hidden field is still a copy the next
        // request would carry, and would not be caught by looking for the value at all.
        Assert.Equal(1, Occurrences(body, token));

        // Nowhere in the store, in any column.
        Assert.DoesNotContain(token, await DumpTokenRowsAsync(), StringComparison.Ordinal);

        // Not on the page when it is fetched again — a refresh must not reproduce it.
        Assert.DoesNotContain(token, await client.GetStringAsync(Page), StringComparison.Ordinal);

        // Nor when the form is submitted again. That mints a second token, and the response shows
        // the new one; the first must not come back with it.
        var again = await SubmitAsync(client, GenerateForm);
        Assert.DoesNotContain(token, await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2, (await GetTokensAsync()).Count);

        AssertNeverLogged(token);
    }

    [Fact]
    public async Task Revoking_a_token_stops_it_authenticating()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var token = await GenerateTokenAsync(client);
        Assert.NotNull(await VerifyAsync("alice", token));

        var stored = Assert.Single(await GetTokensAsync());
        Assert.Equal(RevokedMessage, await RevokeOutcomeAsync(client, stored.Id));

        // Proved through the credential path rather than through the word the list prints. A token
        // reported as revoked on the page while still opening the git remote is the failure that
        // matters, and only this assertion can see it.
        Assert.Null(await VerifyAsync("alice", token));
        Assert.NotNull((await GetTokensAsync()).Single().RevokedAt);
    }

    [Fact]
    public async Task A_member_cannot_revoke_another_members_token_or_learn_that_it_exists()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        var aliceToken = await GenerateTokenAsync(alice);
        var aliceTokenId = Assert.Single(await GetTokensAsync()).Id;

        var bob = await SignInAsync("bob");

        var refused = await RevokeOutcomeAsync(bob, aliceTokenId);
        var absent = await RevokeOutcomeAsync(bob, Guid.NewGuid());

        // Alice's token is untouched and still authenticates — the list view is not the thing being
        // trusted here either.
        Assert.Null(Assert.Single(await GetTokensAsync()).RevokedAt);
        Assert.NotNull(await VerifyAsync("alice", aliceToken));

        // Bob is told the same thing either way, so the form cannot be asked whether an identifier
        // names somebody's token — and he is told the *true* thing. Equality alone is also satisfied
        // by a page that reports both as revoked, which is indistinguishable and a lie.
        Assert.Equal(absent, refused);
        Assert.Equal(NoSuchTokenMessage, refused);
    }

    [Fact]
    public async Task A_member_does_not_see_another_members_tokens()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        await GenerateTokenAsync(alice);
        var aliceTokenId = Assert.Single(await GetTokensAsync()).Id;

        var body = await (await SignInAsync("bob")).GetStringAsync(Page);

        Assert.DoesNotContain(aliceTokenId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No git access tokens yet.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_requires_a_post_carrying_an_antiforgery_token()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await GenerateTokenAsync(client);
        var tokenId = Assert.Single(await GetTokensAsync()).Id;

        // A revoke reachable by GET is triggerable by any page that can make the browser fetch a
        // URL, and an <img> tag is enough.
        var viaGet = await client.GetAsync($"{Page}?_handler={RevokeForm}&RevokeInput.TokenId={tokenId}");
        Assert.Equal(HttpStatusCode.OK, viaGet.StatusCode);
        Assert.Null(Assert.Single(await GetTokensAsync()).RevokedAt);

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, RevokeForm);
        fields.Remove("__RequestVerificationToken");
        fields["RevokeInput.TokenId"] = tokenId.ToString();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, Page, fields)).StatusCode);
        Assert.Null(Assert.Single(await GetTokensAsync()).RevokedAt);
    }

    [Fact]
    public async Task Generating_requires_a_post()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync($"{Page}?_handler={GenerateForm}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetTokensAsync());
    }

    [Fact]
    public async Task Generating_requires_a_post_carrying_an_antiforgery_token()
    {
        // The GET case above only proves a link cannot mint a token. This is the other half: a
        // cross-site form post must not mint one either, and nothing asserted that until now.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, GenerateForm);
        fields.Remove("__RequestVerificationToken");

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, Page, fields)).StatusCode);
        Assert.Empty(await GetTokensAsync());
    }

    [Fact]
    public async Task No_cache_may_keep_a_copy_of_this_page()
    {
        // Shown-once is a promise about the client as much as about the store: the back button
        // re-presents a response the browser kept, credential and all, without asking the server.
        // Until this test the property was held up entirely by headers antiforgery happens to emit
        // — nobody's decision, and one antiforgery change away from being withdrawn in silence.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        AssertNotCacheable(await SubmitAsync(client, GenerateForm));
        AssertNotCacheable(await client.GetAsync(Page));
    }

    [Fact]
    public async Task Revoking_a_token_twice_leaves_the_first_revocation_standing()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        var token = await GenerateTokenAsync(client);
        var tokenId = Assert.Single(await GetTokensAsync()).Id;

        await SubmitAsync(client, RevokeForm, ("RevokeInput.TokenId", tokenId.ToString()));
        var revokedAt = Assert.Single(await GetTokensAsync()).RevokedAt;
        Assert.NotNull(revokedAt);

        // The button is gone from the page by now, so this is a resubmitted form — the back button,
        // or a refresh — arriving at the handler a second time. It must not re-date the revocation,
        // and it must certainly not undo it.
        await SubmitAsync(client, RevokeForm, ("RevokeInput.TokenId", tokenId.ToString()));

        Assert.Equal(revokedAt, Assert.Single(await GetTokensAsync()).RevokedAt);
        Assert.Null(await VerifyAsync("alice", token));
    }

    [Fact]
    public async Task Adding_an_email_associates_it_with_the_signed_in_account()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        Assert.Equal(EmailAddedMessage, await AddEmailOutcomeAsync(client, "alice@example.com"));

        var stored = Assert.Single(await GetEmailsAsync());
        Assert.Equal("alice@example.com", stored.Email);
    }

    [Fact]
    public async Task Adding_the_same_email_again_reports_it_is_already_on_the_account()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await AddEmailOutcomeAsync(client, "alice@example.com");

        Assert.Equal(EmailAlreadyOnAccountMessage, await AddEmailOutcomeAsync(client, "alice@example.com"));
        Assert.Single(await GetEmailsAsync());
    }

    [Fact]
    public async Task An_email_already_on_another_account_is_refused_by_the_real_reason_and_names_no_owner()
    {
        // AD24: the caller is told the true reason — the address is taken — and the page must not
        // leak whom by, so the owning account's own username must not appear anywhere in the
        // response that told bob his address was refused.
        await SeedAccountAsync("alice");
        var alice = await SignInAsync("alice");
        await AddEmailOutcomeAsync(alice, "shared@example.com");

        await SeedAccountAsync("bob");
        var bob = await SignInAsync("bob");

        var body = await SubmitAndReadAsync(bob, AddEmailForm, ("AddEmailInput.Email", "shared@example.com"));

        Assert.Contains(EmailTakenMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await GetEmailsAsync(await AccountIdAsync("bob")));
    }

    [Fact]
    public async Task A_malformed_email_is_refused()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        Assert.Equal(EmailMalformedMessage, await AddEmailOutcomeAsync(client, "not-an-email"));
        Assert.Empty(await GetEmailsAsync());
    }

    [Fact]
    public async Task Removing_an_email_stops_it_being_associated()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await AddEmailOutcomeAsync(client, "alice@example.com");
        var emailId = Assert.Single(await GetEmailsAsync()).Id;

        Assert.Equal(EmailRemovedMessage, await RemoveEmailOutcomeAsync(client, emailId));
        Assert.Empty(await GetEmailsAsync());
    }

    [Fact]
    public async Task The_last_email_on_an_account_can_be_removed()
    {
        // The account model states "zero or more" associated emails explicitly, so this must not
        // be refused the way the last administrator or the last redeemable invitation would be.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await AddEmailOutcomeAsync(client, "alice@example.com");
        var emailId = Assert.Single(await GetEmailsAsync()).Id;

        await RemoveEmailOutcomeAsync(client, emailId);

        Assert.Contains("No git emails associated with your account.", await client.GetStringAsync(Page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_remove_another_members_email_or_learn_that_it_exists()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        await AddEmailOutcomeAsync(alice, "alice@example.com");
        var aliceEmailId = Assert.Single(await GetEmailsAsync()).Id;

        var bob = await SignInAsync("bob");

        var refused = await RemoveEmailOutcomeAsync(bob, aliceEmailId);
        var absent = await RemoveEmailOutcomeAsync(bob, Guid.NewGuid());

        Assert.Single(await GetEmailsAsync());
        Assert.Equal(absent, refused);
        Assert.Equal(NoSuchEmailMessage, refused);
    }

    [Fact]
    public async Task A_member_does_not_see_another_members_emails()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");

        var alice = await SignInAsync("alice");
        await AddEmailOutcomeAsync(alice, "alice@example.com");

        var body = await (await SignInAsync("bob")).GetStringAsync(Page);

        Assert.DoesNotContain("alice@example.com", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No git emails associated with your account.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_an_email_requires_a_post()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync($"{Page}?_handler={AddEmailForm}&AddEmailInput.Email=alice@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await GetEmailsAsync());
    }

    [Fact]
    public async Task Adding_an_email_requires_a_post_carrying_an_antiforgery_token()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, AddEmailForm);
        fields.Remove("__RequestVerificationToken");
        fields["AddEmailInput.Email"] = "alice@example.com";

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, Page, fields)).StatusCode);
        Assert.Empty(await GetEmailsAsync());
    }

    [Fact]
    public async Task Removing_requires_a_post_carrying_an_antiforgery_token()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await AddEmailOutcomeAsync(client, "alice@example.com");
        var emailId = Assert.Single(await GetEmailsAsync()).Id;

        // A removal reachable by GET is triggerable by any page that can make the browser fetch a
        // URL — the same hazard §7.1's token revoke closes.
        var viaGet = await client.GetAsync($"{Page}?_handler={RemoveEmailForm}&RemoveEmailInput.EmailId={emailId}");
        Assert.Equal(HttpStatusCode.OK, viaGet.StatusCode);
        Assert.Single(await GetEmailsAsync());

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, Page, RemoveEmailForm);
        fields.Remove("__RequestVerificationToken");
        fields["RemoveEmailInput.EmailId"] = emailId.ToString();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StaticSsrForm.PostAsync(client, Page, fields)).StatusCode);
        Assert.Single(await GetEmailsAsync());
    }

    [Fact]
    public async Task The_page_still_renders_when_the_stored_account_row_cannot_be_read()
    {
        // The §7 projection hazard, designed out rather than survived: both lists on this page are
        // projections and the username comes off the signed-in principal, so no Account row is ever
        // materialised here and a value-converted timestamp nothing can parse cannot take it down.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await GenerateTokenAsync(client);
        var tokenId = Assert.Single(await GetTokensAsync()).Id;
        await AddEmailOutcomeAsync(client, "alice@example.com");

        await _app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE Accounts SET CreatedAt = 'not-a-timestamp'"));

        var response = await client.GetAsync(Page);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("""<strong class="git-username">alice</strong>""", body, StringComparison.Ordinal);
        Assert.Contains(tokenId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice@example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_posts_nothing_but_the_fields_its_forms_need()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");
        await GenerateTokenAsync(client);

        // A closed set, so an unforeseen *addition* fails. A hidden field carrying a token value
        // into the next request is exactly the shape this refuses, and no list of things that must
        // be absent could have named it in advance. The revoke and remove buttons carry their
        // identifier as a <button> value, which is why "RevokeInput.TokenId" and
        // "RemoveEmailInput.EmailId" are not inputs here — only the one text field is.
        Assert.Equal(
            ["AddEmailInput.Email", "__RequestVerificationToken", "_handler"],
            (await StaticSsrForm.GetFieldNamesAsync(client, Page)).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_navigation_offers_the_account_page_to_a_member_and_to_nobody_else()
    {
        // AD23 left no header bar, so the page needs a way in — and whatever provides it renders for
        // members only, or §6's "no navigation to anonymous visitors" is reopened by hand.
        await SeedAccountAsync("alice");

        var anonymous = await _app.CreateHttpClient().GetStringAsync("/login");
        Assert.DoesNotContain("account", anonymous, StringComparison.OrdinalIgnoreCase);

        AssertLinksTo(await (await SignInAsync("alice")).GetStringAsync("/"), Page);
    }

    /// <summary>Asserts no cache anywhere is permitted to keep this response.</summary>
    /// <remarks>
    /// Asserted semantically rather than against a header string, because two writers set this
    /// header — antiforgery and <c>Account.ForbidCaching</c> — and a literal comparison would pin
    /// whichever of them happened to run last, plus its exact spelling, instead of the property that
    /// matters. (Measured on ASP.NET Core 10, the two currently agree to the byte:
    /// <c>no-store, no-cache</c>. That is precisely why a string comparison would be misleading —
    /// it would pass while asserting nothing about who set it, and would break on a reordering that
    /// changes no meaning.) <c>Public</c> and <c>MaxAge</c> are asserted too so the assertion fails
    /// on a directive that was added rather than only on one that was removed.
    /// </remarks>
    private static void AssertNotCacheable(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;

        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.NoStore, $"Cache-Control: {cacheControl}");
        Assert.True(cacheControl.NoCache, $"Cache-Control: {cacheControl}");
        Assert.False(cacheControl.Public, $"Cache-Control: {cacheControl}");
        Assert.Null(cacheControl.MaxAge);

        Assert.Contains(response.Headers.Pragma, pragma => pragma.Name == "no-cache");
    }

    /// <summary>How many times <paramref name="value"/> appears in <paramref name="html"/>.</summary>
    private static int Occurrences(string html, string value) =>
        (html.Length - html.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    /// <summary>Asserts the markup offers an anchor resolving to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Resolved against the site's base rather than string-matched, because a nav link renders
    /// <c>href="account"</c> — and because §6's blocker hid from two independent patterns that both
    /// required a quoted href. The anchor count is asserted first so a pattern that has gone blind
    /// fails here instead of quietly reporting that nothing links anywhere.
    /// </remarks>
    private static void AssertLinksTo(string html, string path)
    {
        var anchors = AnchorTag().Matches(html).Select(match => match.Value).ToList();
        Assert.NotEmpty(anchors);

        Assert.Contains(
            anchors,
            anchor => HrefAttribute().Match(anchor) is { Success: true } href
                && new Uri(ZeroWikiAppFactory.BaseAddress, href.Groups["href"].Value).AbsolutePath == path);
    }

    /// <summary>Asserts a secret reached no sink the running application logs to.</summary>
    /// <remarks>
    /// Read from <see cref="CapturingLoggerProvider.Written"/> rather than
    /// <c>Messages</c>: a value handed to <c>BeginScope</c> reaches a structured sink while
    /// appearing in no rendered message, so the message-only form is the weaker instrument.
    /// The instrument is checked before it is trusted (AD19) — a capture that saw nothing, or one
    /// filtered below the request log, would pass by being empty, and the request log is precisely
    /// where a credential that leaked into a URL would show up.
    /// </remarks>
    private void AssertNeverLogged(string secret)
    {
        var written = _app.Logs.Written.ToList();

        Assert.Contains(written, entry => entry.Contains(Page, StringComparison.Ordinal));
        Assert.DoesNotContain(written, entry => entry.Contains(secret, StringComparison.Ordinal));
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

    /// <summary>Generates a token through the page and returns the plaintext it rendered once.</summary>
    private static async Task<string> GenerateTokenAsync(HttpClient client)
    {
        var body = await (await SubmitAsync(client, GenerateForm)).Content.ReadAsStringAsync();
        var rendered = GitTokenValue().Match(body);
        Assert.True(rendered.Success, $"The generated token was not rendered.\n{body}");

        return WebUtility.HtmlDecode(rendered.Groups["token"].Value);
    }

    /// <summary>Posts a revoke and returns the outcome the page reported.</summary>
    private static async Task<string> RevokeOutcomeAsync(HttpClient client, Guid tokenId)
    {
        var response = await SubmitAsync(client, RevokeForm, ("RevokeInput.TokenId", tokenId.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var outcome = RevocationOutcome().Match(body);
        Assert.True(outcome.Success, $"The revocation outcome was not rendered.\n{body}");

        return outcome.Groups["text"].Value.Trim();
    }

    /// <summary>Posts an add-email and returns the outcome the page reported.</summary>
    private static async Task<string> AddEmailOutcomeAsync(HttpClient client, string email)
    {
        var body = await SubmitAndReadAsync(client, AddEmailForm, ("AddEmailInput.Email", email));

        var outcome = GitEmailAddOutcomeText().Match(body);
        Assert.True(outcome.Success, $"The add-email outcome was not rendered.\n{body}");

        return WebUtility.HtmlDecode(outcome.Groups["text"].Value.Trim());
    }

    /// <summary>Posts a remove-email and returns the outcome the page reported.</summary>
    private static async Task<string> RemoveEmailOutcomeAsync(HttpClient client, Guid emailId)
    {
        var body = await SubmitAndReadAsync(client, RemoveEmailForm, ("RemoveEmailInput.EmailId", emailId.ToString()));

        var outcome = GitEmailRemovalOutcome().Match(body);
        Assert.True(outcome.Success, $"The remove-email outcome was not rendered.\n{body}");

        return outcome.Groups["text"].Value.Trim();
    }

    private static async Task<string> SubmitAndReadAsync(
        HttpClient client,
        string formName,
        params (string Name, string Value)[] extraFields)
    {
        var response = await SubmitAsync(client, formName, extraFields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<Guid> SeedAccountAsync(string username)
    {
        var id = Guid.NewGuid();

        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = id,
                Username = username,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = username,
                CreatedAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

        return id;
    }

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

    /// <summary>Resolves a presented username + token the way the git remote will (§8).</summary>
    private async Task<AuthenticatedAccount?> VerifyAsync(string username, string token) =>
        await _app.WithDbAsync(db =>
            new GitTokenService(db, _tokenGenerator, TimeProvider.System).VerifyAsync(username, token));

    private async Task<IReadOnlyList<GitToken>> GetTokensAsync() =>
        await _app.WithDbAsync(db => db.GitTokens.AsNoTracking().ToListAsync());

    /// <summary>Every git email row across every account.</summary>
    private async Task<IReadOnlyList<GitEmail>> GetEmailsAsync() =>
        await _app.WithDbAsync(db => db.GitEmails.AsNoTracking().ToListAsync());

    private async Task<IReadOnlyList<GitEmail>> GetEmailsAsync(Guid accountId) =>
        await _app.WithDbAsync(db =>
            db.GitEmails.AsNoTracking().Where(e => e.AccountId == accountId).ToListAsync());

    private async Task<Guid> AccountIdAsync(string username) =>
        await _app.WithDbAsync(db => db.Accounts
            .AsNoTracking()
            .Where(a => a.Username == username)
            .Select(a => a.Id)
            .SingleAsync());

    /// <summary>
    /// Every column of every git token row, so "the plaintext is not stored" is asserted against the
    /// whole row rather than against the one column it was least likely to be in.
    /// </summary>
    private async Task<string> DumpTokenRowsAsync() =>
        string.Join('\n', await _app.WithDbAsync(async db =>
        {
            var rows = new List<string>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id || '|' || AccountId || '|' || TokenHash || '|' || CreatedAt "
                + "|| '|' || COALESCE(RevokedAt, '') FROM GitTokens";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }));

    [GeneratedRegex("""class="git-token">(?<token>[^<]+)<""")]
    private static partial Regex GitTokenValue();

    [GeneratedRegex("""class="revocation-outcome"[^>]*>(?<text>[^<]*)<""")]
    private static partial Regex RevocationOutcome();

    [GeneratedRegex("""class="git-email-add-outcome"[^>]*>(?<text>[^<]*)<""")]
    private static partial Regex GitEmailAddOutcomeText();

    [GeneratedRegex("""class="git-email-removal-outcome"[^>]*>(?<text>[^<]*)<""")]
    private static partial Regex GitEmailRemovalOutcome();

    [GeneratedRegex("""<a\b[^>]*>""")]
    private static partial Regex AnchorTag();

    [GeneratedRegex("""\shref(?:="(?<href>[^"]*)")?""")]
    private static partial Regex HrefAttribute();
}
