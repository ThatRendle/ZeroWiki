using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises the browser editing surface — <c>WikiPage.razor</c>'s <c>edit</c> mode and
/// <c>PageEditor.razor</c> (D17, <c>specs/content-editing/spec.md</c>, §6 block D4) — over HTTP against
/// the real application, for the same reason <see cref="WikiPageTests"/> does: a form whose rendered
/// field names have drifted from its binder posts nothing and every unit test stays green.
/// </summary>
public sealed class WikiPageEditorTests : IDisposable
{
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly GitProcessRunner _git = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Opening_the_editor_for_an_address_with_no_page_yet_presents_an_empty_form_declaring_absent()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync("/wiki/brand-new?edit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Create page", body, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.BaseRevisionToken\" value=\"\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_the_editor_for_an_existing_page_presents_its_content_and_base_revision()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Existing body.");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync("/wiki/page?edit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Edit page", body, StringComparison.Ordinal);
        Assert.Contains("Existing body.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.BaseRevisionToken\" value=\"\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_the_editor_for_an_ambiguous_address_is_refused_rather_than_opening_an_editor()
    {
        // The "Editing surface refuses an address that identifies no single file" scenario
        // (specs/content-editing/spec.md) — distinct from New, which is "no page here yet".
        await SeedAccountAsync("alice");
        await WritePageAsync("a_ b.md", "First claimant.");
        await WritePageAsync("a _b.md", "Second claimant.");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync("/wiki/a___b?edit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("cannot be edited", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<textarea", body, StringComparison.Ordinal);
        Assert.DoesNotContain("First claimant.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rendered_form_posts_back_to_the_address_it_was_served_from_query_string_included()
    {
        // Fed to the machine rather than assumed: Blazor's EditForm is proven here to render an explicit
        // `action` carrying the current path AND the `edit` query flag, which is what makes the save
        // land back on the same edit mode rather than on the plain view route.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var html = await client.GetStringAsync("/wiki/brand-new?edit");

        Assert.Contains("action=\"/wiki/brand-new?edit\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_new_page_creates_it_and_the_view_shows_it_afterwards()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/brand-new?edit");
        var response = await StaticSsrForm.PostAsync(client, "/wiki/brand-new?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "# New page\n\nHello."),
        ]));

        HttpAssertions.AssertRedirectedTo("/wiki/brand-new", response);

        var view = await client.GetAsync("/wiki/brand-new");
        var viewBody = await view.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Contains("Hello.", viewBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_an_existing_page_on_its_current_base_updates_it()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original.");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");
        var response = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "Updated body."),
        ]));

        HttpAssertions.AssertRedirectedTo("/wiki/page", response);

        var view = await client.GetAsync("/wiki/page");
        var viewBody = await view.Content.ReadAsStringAsync();
        Assert.Contains("Updated body.", viewBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Original.", viewBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_base_revision_is_rejected_with_the_members_own_text_preserved_and_nothing_written()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original.");
        var client = await SignInAsync("alice");

        // Captures "Original."'s blob sha as the declared base revision.
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");

        // A second writer advances the page underneath this edit — direct git, the same "changed
        // without the app being told" scenario WikiPageTests already exercises for the read path.
        await WritePageAsync("page.md", "Changed by someone else.");

        var response = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "My unsaved edit."),
        ]));

        // Post/Redirect/Get (D17, §6 block D4 continuation): the failure response itself must be a
        // redirect, not the rendered error — a re-rendered POST response is what made a reload raise
        // the browser's "resubmit this form?" prompt, the defect the Product Owner found.
        var body = await AssertPrgRedirectAndFollowAsync("/wiki/page", response, client);

        Assert.Contains("changed underneath your save", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("My unsaved edit.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed by someone else.", body, StringComparison.Ordinal);

        var view = await client.GetAsync("/wiki/page");
        Assert.Contains("Changed by someone else.", await view.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_that_becomes_ambiguous_between_load_and_save_is_refused_distinctly_from_a_conflict()
    {
        // D17's own reason the ambiguity refusal must live in the save path, not only the surface: an
        // Obsidian push can land this exact collision while a member is mid-edit.
        await SeedAccountAsync("alice");
        await WritePageAsync("Chapter_1.md", "Original.");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/Chapter__1?edit");

        await WritePageAsync("Chapter  1.md", "Second claimant.");

        var response = await StaticSsrForm.PostAsync(client, "/wiki/Chapter__1?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "My edit, now unsavable at this address."),
        ]));

        var body = await AssertPrgRedirectAndFollowAsync("/wiki/Chapter__1", response, client);

        Assert.Contains("no longer identifies a single file", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed underneath your save", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("My edit, now unsavable at this address.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repository_being_busy_is_reported_distinctly_and_nothing_is_written()
    {
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/brand-new?edit");

        var paths = new ContentPaths(_app.DataRoot);
        using var externalLock = await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30));

        var response = await StaticSsrForm.PostAsync(client, "/wiki/brand-new?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "Never written."),
        ]));

        var body = await AssertPrgRedirectAndFollowAsync("/wiki/brand-new", response, client);

        Assert.Contains("repository is busy", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never written.", body, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_app.DataRoot, "wiki", "docs", "brand-new.md")));
    }

    [Fact]
    public async Task A_failed_commit_is_reported_distinctly_with_the_working_tree_restored()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original.");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");
        await InstallFailingPreCommitHookAsync();

        var response = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "This will never be committed."),
        ]));

        var body = await AssertPrgRedirectAndFollowAsync("/wiki/page", response, client);

        Assert.Contains("save failed", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This will never be committed.", body, StringComparison.Ordinal);
        Assert.Equal("Original.", await File.ReadAllTextAsync(Path.Combine(_app.DataRoot, "wiki", "docs", "page.md")));
    }

    [Fact]
    public async Task A_failed_rollback_is_reported_distinctly_from_an_ordinary_failed_save()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original.");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");
        await InstallFailingPreCommitHookThatLocksDocsAsync();

        try
        {
            var response = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", fields.Concat(
            [
                KeyValuePair.Create("Input.Content", "This will never be committed, and the rollback also fails."),
            ]));

            var body = await AssertPrgRedirectAndFollowAsync("/wiki/page", response, client);

            Assert.Contains("automatic recovery also failed", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "This will never be committed, and the rollback also fails.", body, StringComparison.Ordinal);
        }
        finally
        {
            MakeDocsWritableAgain();
        }
    }

    [Fact]
    public async Task The_successful_save_redirect_carries_no_draft_token()
    {
        // Confirms the success path's shape is unchanged by this round's fix -- it already redirected
        // before the PRG failure fix, and must go on doing so without picking up a draft token it has
        // no use for.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/brand-new?edit");
        var response = await StaticSsrForm.PostAsync(client, "/wiki/brand-new?edit", fields.Concat(
        [
            KeyValuePair.Create("Input.Content", "Saved content."),
        ]));

        HttpAssertions.AssertRedirectedTo("/wiki/brand-new", response);
        var (_, query) = ParseRedirectLocation(response);
        Assert.DoesNotContain("draft=", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_draft_token_degrades_to_a_fresh_editor_with_an_expiry_notice()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Committed content.");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync("/wiki/page?edit&draft=this-token-was-never-issued");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("draft expired", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Committed content.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_draft_token_degrades_to_a_fresh_editor_rather_than_reviving_stale_text()
    {
        // Exercised through PageEditor.razor's own read path, not EditDraftStore's TTL directly
        // (EditDraftStoreTests owns that): a token whose draft the store has already forgotten, at the
        // surface, must show fresh committed content plus the expiry notice -- never silently look
        // like an ordinary, successful fresh load, and never revive text that no longer exists.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Committed content.");
        var client = await SignInAsync("alice");

        // A token in the right shape (issued by the real generator) but never saved into this run's
        // store is indistinguishable, from TryGet's perspective, from one that already expired --
        // EditDraftStoreTests proves the TTL boundary itself; this proves the surface's degradation.
        var neverSavedToken = new SecretTokenGenerator().Generate().Plaintext;

        var response = await client.GetAsync($"/wiki/page?edit&draft={Uri.EscapeDataString(neverSavedToken)}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("draft expired", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Committed content.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_belonging_to_another_account_degrades_the_same_way_rather_than_leaking_their_draft()
    {
        await SeedAccountAsync("alice");
        await SeedAccountAsync("bob");
        await WritePageAsync("page.md", "Committed content.");

        var alice = await SignInAsync("alice");
        var bob = await SignInAsync("bob");

        // Alice's save fails (repository busy) and picks up a draft token via PRG.
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(alice, "/wiki/page?edit");
        var paths = new ContentPaths(_app.DataRoot);
        string aliceToken;
        using (await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30)))
        {
            var failedSave = await StaticSsrForm.PostAsync(alice, "/wiki/page?edit", fields.Concat(
            [
                KeyValuePair.Create("Input.Content", "Alices secret draft text."),
            ]));
            var (_, query) = ParseRedirectLocation(failedSave);
            aliceToken = ExtractDraftToken(query);
        }

        // Bob, a different signed-in account, gets hold of the same token (e.g. via a referrer or a
        // shared log line) and requests it for himself.
        var bobResponse = await bob.GetAsync($"/wiki/page?edit&draft={Uri.EscapeDataString(aliceToken)}");
        var bobBody = await bobResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, bobResponse.StatusCode);
        Assert.Contains("draft expired", bobBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Committed content.", bobBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Alices secret draft text.", bobBody, StringComparison.Ordinal);

        // Alice herself can still recover it -- proves the degradation above is the ownership check,
        // not the token having been consumed or otherwise invalidated by Bob's attempt.
        var aliceResponse = await alice.GetAsync($"/wiki/page?edit&draft={Uri.EscapeDataString(aliceToken)}");
        var aliceBody = await aliceResponse.Content.ReadAsStringAsync();
        Assert.Contains("Alices secret draft text.", aliceBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_concurrent_failed_saves_of_the_same_page_keep_distinct_recoverable_drafts()
    {
        // The two-tab case the token exists for: the conflict scenario in
        // A_stale_base_revision_is_rejected... already proves this end to end for one tab; this proves
        // two tabs' OWN failed-save drafts do not clobber each other.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original.");
        var client = await SignInAsync("alice");

        var tabA = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");
        var tabB = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/page?edit");

        // Both tabs now hold "Original."'s base revision. Advancing the page once means BOTH posts
        // below are stale-base conflicts, each producing its own draft.
        await WritePageAsync("page.md", "Changed by someone else.");

        var responseA = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", tabA.Concat(
        [
            KeyValuePair.Create("Input.Content", "Tab A text."),
        ]));
        var responseB = await StaticSsrForm.PostAsync(client, "/wiki/page?edit", tabB.Concat(
        [
            KeyValuePair.Create("Input.Content", "Tab B text."),
        ]));

        var (_, queryA) = ParseRedirectLocation(responseA);
        var (_, queryB) = ParseRedirectLocation(responseB);
        var tokenA = ExtractDraftToken(queryA);
        var tokenB = ExtractDraftToken(queryB);

        Assert.NotEqual(tokenA, tokenB);

        var bodyA = await (await client.GetAsync($"/wiki/page?edit&draft={Uri.EscapeDataString(tokenA)}")).Content.ReadAsStringAsync();
        var bodyB = await (await client.GetAsync($"/wiki/page?edit&draft={Uri.EscapeDataString(tokenB)}")).Content.ReadAsStringAsync();

        Assert.Contains("Tab A text.", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("Tab B text.", bodyA, StringComparison.Ordinal);
        Assert.Contains("Tab B text.", bodyB, StringComparison.Ordinal);
        Assert.DoesNotContain("Tab A text.", bodyB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_globally_refused_draft_re_renders_inline_preserving_every_character_rather_than_discarding_it()
    {
        // The global backstop (D17, §6 block D4 continuation round two): reached honestly here by
        // EditDraftStore.MaxEntries distinct accounts each holding one live draft, stashed directly
        // through the app's own singleton rather than via hundreds of real sign-ins and failed HTTP
        // saves — EditDraftStoreTests owns proving the backstop's own refuse-not-steal behaviour in
        // isolation; this test only needs ONE genuine failed save landing on an already-full store.
        //
        // §6 remediation (Blocker 3, Product Owner decision): "never discard what the member typed"
        // stays absolute with no carve-out, so Post/Redirect/Get yields here rather than the other way
        // round -- there is nowhere left to stash the text for a follow-up GET to recover, so the ONLY
        // way to keep every character is to re-render this same POST response in place. This replaces
        // the previous version of this test, which asserted the discarding (redirect-to-a-bare-"Create
        // page") behaviour as correct.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        using (var scope = _app.Services.CreateScope())
        {
            var draftStore = scope.ServiceProvider.GetRequiredService<EditDraftStore>();
            for (var i = 0; i < EditDraftStore.MaxEntries; i++)
            {
                draftStore.Save(Guid.NewGuid(), new RouteValue($"filler-{i}"), "filler", "sha", "m");
            }
        }

        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/wiki/brand-new?edit");

        var paths = new ContentPaths(_app.DataRoot);
        HttpResponseMessage response;
        using (await RepositoryWriteLock.AcquireAsync(paths.LockFilePath, TimeSpan.FromSeconds(30)))
        {
            response = await StaticSsrForm.PostAsync(client, "/wiki/brand-new?edit", fields.Concat(
            [
                KeyValuePair.Create("Input.Content", "Alices unsaved text."),
            ]));
        }

        // Not a redirect: there is no draft token to carry, and no follow-up GET could recover text
        // that was never stashed anywhere.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Alices unsaved text.", body, StringComparison.Ordinal);
        Assert.Contains("still shown below exactly as you typed it", body, StringComparison.OrdinalIgnoreCase);
        // Distinct from the ordinary expired-draft wording -- a member reading either must be able to
        // tell "never got a chance to be saved" from "was saved, then timed out."
        Assert.DoesNotContain("draft expired", body, StringComparison.OrdinalIgnoreCase);
        // The fixture never wrote brand-new.md, so this is still the create form -- but now with the
        // member's own text in it, not a bare, unexplained empty one.
        Assert.Contains("Create page", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_link_is_offered_on_an_existing_page_and_a_create_link_on_a_missing_one()
    {
        // 6.1's "way in" — otherwise the surface is unreachable in a browser without typing the query
        // string by hand.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Body.");
        var client = await SignInAsync("alice");

        var existing = await client.GetStringAsync("/wiki/page");
        Assert.Contains("href=\"/wiki/page?edit\"", existing, StringComparison.Ordinal);

        var missing = await client.GetStringAsync("/wiki/does-not-exist");
        Assert.Contains("href=\"/wiki/does-not-exist?edit\"", missing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a#b", "a%23b")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a%20b", "a%2520b")]
    public async Task The_edit_link_is_built_from_the_canonical_encoded_route_not_the_decoded_request_path(
        string fileNameWithoutExtension, string canonicalEncodedRoute)
    {
        // Blocker 2, §6 remediation: the previous version of WikiPage.razor built its edit href from
        // context.Request.Path.Value, which ASP.NET Core has already percent-decoded once -- correct
        // only when a route's encoded and decoded forms happen to be identical, which every one of the
        // twelve pre-existing tests in this file is (page, brand-new, does-not-exist, Chapter__1, ...).
        // These three routes are chosen because they are not: '#', '?' and a literal '%' are exactly
        // what PageRouteCodec.EncodeSegment's second layer exists to escape (D12), and building the
        // href from the decoded path would have produced "/wiki/a#b?edit", "/wiki/a?b?edit" and
        // "/wiki/a%20b?edit" respectively -- three different, wrong addresses (verified in the DEVLOG's
        // own reproduction table for this remediation).
        await SeedAccountAsync("alice");
        await WritePageAsync($"{fileNameWithoutExtension}.md", "Body.");
        var client = await SignInAsync("alice");

        var body = await client.GetStringAsync($"/wiki/{canonicalEncodedRoute}");

        Assert.Contains($"href=\"/wiki/{canonicalEncodedRoute}?edit\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts <paramref name="response"/> is a Post/Redirect/Get redirect to <paramref name="expectedPath"/>
    /// carrying a <c>draft</c> token, follows it with <paramref name="client"/>, and returns the
    /// follow-up GET's rendered body. The status code and the <c>Location</c> header are asserted here,
    /// not only the eventual body — the exact gap that let this block's original defect (D17, §6 block
    /// D4 continuation) pass twelve body-only tests.
    /// </summary>
    private static async Task<string> AssertPrgRedirectAndFollowAsync(
        string expectedPath, HttpResponseMessage response, HttpClient client)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var (path, query) = ParseRedirectLocation(response);
        Assert.Equal(expectedPath, path);
        Assert.Contains("edit", query, StringComparison.Ordinal);
        var token = ExtractDraftToken(query);

        var followUp = await client.GetAsync($"{expectedPath}?edit&draft={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);

        return await followUp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// <paramref name="response"/>'s <c>Location</c> header split into path and query, handling both an
    /// absolute and a relative form the way <see cref="HttpAssertions.AssertRedirectedTo"/> already does
    /// — a relative <see cref="Uri"/> throws on <see cref="Uri.AbsolutePath"/>/<see cref="Uri.Query"/>.
    /// </summary>
    private static (string Path, string Query) ParseRedirectLocation(HttpResponseMessage response)
    {
        var location = Assert.IsType<Uri>(response.Headers.Location);
        if (location.IsAbsoluteUri)
        {
            return (location.AbsolutePath, location.Query);
        }

        var originalString = location.OriginalString;
        var queryIndex = originalString.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0
            ? (originalString, string.Empty)
            : (originalString[..queryIndex], originalString[queryIndex..]);
    }

    private static string ExtractDraftToken(string query)
    {
        var match = Regex.Match(query, @"[?&]draft=(?<token>[^&]+)");
        Assert.True(match.Success, $"No draft token in redirect query '{query}'.");
        return Uri.UnescapeDataString(match.Groups["token"].Value);
    }

    private void MakeDocsWritableAgain()
    {
        var workingTree = Path.Combine(_app.DataRoot, "wiki", "docs");
        if (!OperatingSystem.IsWindows() && Directory.Exists(workingTree))
        {
            File.SetUnixFileMode(
                workingTree,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task InstallFailingPreCommitHookAsync()
    {
        var hooksDirectory = Path.Combine(_app.DataRoot, "wiki", ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, "#!/bin/sh\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task InstallFailingPreCommitHookThatLocksDocsAsync()
    {
        var workingTree = Path.Combine(_app.DataRoot, "wiki", "docs");
        var hooksDirectory = Path.Combine(_app.DataRoot, "wiki", ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hookPath = Path.Combine(hooksDirectory, "pre-commit");
        await File.WriteAllTextAsync(hookPath, $"#!/bin/sh\nchmod 555 '{workingTree}'\nexit 1\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task WritePageAsync(string relativePath, string content)
    {
        await _app.CreateHttpClient().GetAsync("/");

        var repositoryRoot = Path.Combine(_app.DataRoot, "wiki");
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);

        var author = new GitAuthor("Test Author", "author@zerowiki.example");
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "write " + relativePath], author.ToEnvironmentVariables());
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
}
