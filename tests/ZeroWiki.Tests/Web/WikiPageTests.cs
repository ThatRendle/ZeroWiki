using System.Net;
using Microsoft.EntityFrameworkCore;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Exercises <c>/wiki/{*Route}</c> over HTTP against the real application — real routing, real
/// Markdown rendering, and a real git repository — so the double-decode hazard named in the block 3b
/// DEVLOG brief is proven against ASP.NET Core's actual routing behaviour, not a unit-level stand-in
/// for it.
/// </summary>
public sealed class WikiPageTests : IDisposable
{
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly GitProcessRunner _git = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_anonymous_visitor_gets_the_landing_page_instead_of_a_page()
    {
        await WritePageAsync("page.md", "# Hello\n\nBody text.");

        var response = await _app.CreateHttpClient().GetAsync("/wiki/page");

        await HttpAssertions.AssertIsAnonymousLandingPageAsync(response);
    }

    [Fact]
    public async Task A_signed_in_member_sees_the_rendered_markdown_body()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "# Heading\n\nSome *emphasised* text.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Heading</h1>", body, StringComparison.Ordinal);
        Assert.Contains("<em>emphasised</em>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nested_page_is_addressable_by_its_encoded_path()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync(Path.Combine("Project Notes", "Kick Off.md"), "Nested body.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/Project_Notes/Kick_Off");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Nested body.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_whose_filename_needs_percent_encoding_is_served_at_its_double_encoded_route_and_not_the_wrong_file()
    {
        // The exact hazard block 3b's brief named: a file literally named "a%20b.md" encodes to the
        // canonical route "a%2520b" (D12 layer 2 escapes the literal '%'). If the page double-decoded
        // ASP.NET Core's already-decoded route value, this request would resolve "a b.md" instead.
        await SeedAccountAsync("alice");
        await WritePageAsync("a%20b.md", "Correct file.");

        var client = await SignInAsync("alice");
        var response = await client.GetAsync("/wiki/a%2520b");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Correct file.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ambiguous_route_names_both_claimants_rather_than_reading_as_not_found()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("a_ b.md", "First claimant.");
        await WritePageAsync("a _b.md", "Second claimant.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/a___b");
        var body = await response.Content.ReadAsStringAsync();

        // 409, not 404: the address exists and resolves to more than one file, a different condition
        // than nothing being there at all. IStatusCodePagesFeature.Enabled = false is what keeps this
        // status code from silently discarding the body below in favour of NotFound.razor's generic text.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ambiguous", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a_ b.md", body, StringComparison.Ordinal);
        Assert.Contains("a _b.md", body, StringComparison.Ordinal);
        Assert.DoesNotContain("First claimant.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second claimant.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sole_claimant_whose_route_does_not_identify_it_is_refused_rather_than_served()
    {
        // D12's sharpest fixture: "Chapter  1.md" (two spaces) is the tree's only claimant of
        // "Chapter__1", yet that route decodes to "Chapter_1.md" — a file that does not exist. Serving
        // it anyway is the silent-wrong-file hazard D12 exists to prevent.
        await SeedAccountAsync("alice");
        await WritePageAsync("Chapter  1.md", "Should never be served at this route.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/Chapter__1");
        var body = await response.Content.ReadAsStringAsync();

        // Same D12 failure class as the two-claimant case above, and the same status code: the route
        // exists (its sole claimant is right there on disk) but does not identify it, which D12 treats
        // as one "cannot be trusted to name one specific file" failure, not two.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Chapter  1.md", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Should never be served at this route.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_with_no_matching_page_is_reported_as_not_found()
    {
        await SeedAccountAsync("alice");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        // 200, not 404: under Routes.razor's *current* NotFoundPage configuration, ASP.NET Core's
        // Blazor Web App intercepts a Static SSR response set to 404 and discards the component's own
        // body regardless of IStatusCodePagesFeature — reproduced against the real app and documented
        // in WikiPage.razor's remarks (block 3b round 2), which also names the lever (removing/reworking
        // that NotFoundPage setting) that would change this. The 409 path above proves the escape hatch
        // genuinely works when the status code isn't 404, so this isn't 200-by-default; it's the one
        // outcome where 404 is unusable given today's app configuration, not a property of Blazor or
        // HTTP generally.
        //
        // The reviewer's nit (block 3b round 1): a loose case-insensitive "not found" substring match
        // can't tell this page's own not-found branch from a swallowed-and-substituted NotFound.razor
        // render, because NotFound.razor's own heading ("Not Found") also matches it — exactly the
        // heisenbug this test's earlier version lived through unnoticed. Asserting the exact wording
        // this page's own branch uses, which NotFound.razor's text does not contain, makes the
        // assertion actually falsifiable by a regression of the same kind.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("There is no page at this address.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hostile_route_is_refused_rather_than_raising_an_unhandled_error()
    {
        // A raw %00 never reaches this page at all — ASP.NET Core's own request-line parser
        // (Microsoft.AspNetCore.Internal.UrlDecoder) throws "The path contains null characters"
        // before routing runs, confirmed empirically against this exact request; PageRouteCodecTests
        // covers the NUL/control-character refusal directly against TryDecodeRouteValue instead. This
        // test exercises a route that *does* reach the page — a control character the HTTP layer lets
        // through — and proves the request completes (200, not-found content; see the "genuinely
        // unmatched" case above for why not 404) rather than 500ing.
        await SeedAccountAsync("alice");
        var client = await SignInAsync("alice");

        var response = await client.GetAsync("/wiki/%01");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("There is no page at this address.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_changed_directly_with_git_after_the_page_was_already_served_is_reflected_without_a_restart()
    {
        // D15's freshness scenario end to end: a writer that never notifies the app -- an `updateInstead`
        // push or an operator committing on the volume, simulated here by committing straight through
        // git rather than through the app's own save path -- must still be reflected on the very next
        // request, with no restart in between.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Original body.", authorName: "First Author");
        var client = await SignInAsync("alice");

        var firstResponse = await client.GetAsync("/wiki/page");
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.Contains("Original body.", firstBody, StringComparison.Ordinal);
        Assert.Contains("First Author", firstBody, StringComparison.Ordinal);

        await WritePageAsync("page.md", "Updated body.", authorName: "Second Author");

        var secondResponse = await client.GetAsync("/wiki/page");
        var secondBody = await secondResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Contains("Updated body.", secondBody, StringComparison.Ordinal);
        Assert.Contains("Second Author", secondBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Original body.", secondBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_deleted_directly_with_git_stops_being_served_without_a_restart()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Should disappear.");
        var client = await SignInAsync("alice");

        var beforeResponse = await client.GetAsync("/wiki/page");
        Assert.Contains("Should disappear.", await beforeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var repositoryRoot = Path.Combine(_app.DataRoot, "wiki");
        await _git.RunOrThrowAsync(repositoryRoot, ["rm", "docs/page.md"]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "remove page.md"],
            new GitAuthor("Test Author", "author@zerowiki.example").ToEnvironmentVariables());

        var afterResponse = await client.GetAsync("/wiki/page");
        var afterBody = await afterResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        Assert.Contains("There is no page at this address.", afterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Should disappear.", afterBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorship_is_read_from_git_history()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Body.", authorName: "Some Author", authorEmail: "author@zerowiki.example");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Some Author", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_git_email_is_attributed_to_the_raw_identity()
    {
        // Spec scenario, named explicitly rather than left implicit in the test above:
        // "author@zerowiki.example" matches neither of GitIdentityResolver's synthetic shapes (wrong
        // domain) nor any registered GitEmail, so the page must show exactly what git reports.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Body.", authorName: "Unmapped Pusher", authorEmail: "unmapped@laptop.example");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Unmapped Pusher", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_originated_edit_by_a_known_git_email_is_attributed_to_the_account()
    {
        // §8.3 end to end: a commit's raw author name is whatever the pusher's local git config says
        // (here deliberately different from the account's username) — the page must show the account
        // the registered GitEmail resolves to, not the raw pushed name.
        var aliceId = await SeedAccountAsync("alice");
        await _app.WithDbAsync(async db =>
        {
            db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = aliceId, Email = "alice@laptop.example" });
            await db.SaveChangesAsync();
        });
        await WritePageAsync("page.md", "Body.", authorName: "Alice's Laptop", authorEmail: "alice@laptop.example");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("alice", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice's Laptop", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_squatted_git_email_does_not_change_what_the_page_attributes_another_members_edit_to()
    {
        // The security property D19 §5 exists for, proven end to end: bob registers alice's synthetic
        // address (account+<id>@zerowiki.org, since "alice" is a legal dot-atom the outbound side would
        // use directly — so alice's own synthetic address is bare "alice@zerowiki.org") as one of his
        // own git emails. A commit whose author line is that exact address must still attribute to
        // alice, never to bob, regardless of what he registered.
        await SeedAccountAsync("alice");
        var bobId = await SeedAccountAsync("bob");
        var syntheticAliceAddress = $"alice@{ContentAuthorshipOptions.DefaultHostDomain}";
        await _app.WithDbAsync(async db =>
        {
            db.GitEmails.Add(new GitEmail { Id = Guid.NewGuid(), AccountId = bobId, Email = syntheticAliceAddress });
            await db.SaveChangesAsync();
        });
        await WritePageAsync("page.md", "Body.", authorName: "Not Alice", authorEmail: syntheticAliceAddress);

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        // Two independent DataProtection-protected (hence effectively random) markers ride every
        // response for a page that renders ChangedOnDiskIndicator (D19 §3): the end-of-document
        // persisted-component-state comment and each InteractiveServer component's own start/end
        // marker comment. Either can coincidentally contain "bob" and fail the case-insensitive check
        // below on unmutated, correct code — both must be stripped first, exactly as
        // HttpAssertions.StripInteractiveComponentMarkers's own remarks record (found by reproducing
        // the flake, not merely by reading the reviewer's report of it).
        var body = HttpAssertions.StripInteractiveComponentMarkers(
            HttpAssertions.StripPersistedComponentState(await response.Content.ReadAsStringAsync()));

        Assert.Contains("alice", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not Alice", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frontmatter_title_is_used_as_the_page_title_and_tags_are_shown()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync(
            "page.md",
            """
            ---
            title: A Custom Title
            tags:
              - example
              - demo
            ---
            Body text.
            """);

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("A Custom Title", body, StringComparison.Ordinal);
        Assert.Contains("example", body, StringComparison.Ordinal);
        Assert.Contains("demo", body, StringComparison.Ordinal);

        // The frontmatter block itself must never reach the rendered body.
        Assert.DoesNotContain("title: A Custom Title", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedded_script_is_shown_as_text_rather_than_executed()
    {
        // D13: raw HTML disabled at the pipeline, not sanitized. This is the only defence, so it must
        // hold for content that arrives exactly the way a pushed page would.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Before.\n\n<script>alert('xss')</script>\n\nAfter.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert", body, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_script_bearing_link_destination_is_refused_end_to_end()
    {
        // S1 (block 3 remediation) end to end: DisableHtml() never governed link/image *destinations*,
        // so a real request through the real pipeline is what actually proves the allow-list is wired
        // in, not just that MarkdownLinkAllowList works in isolation (MarkdownLinkAllowListTests covers
        // that). Asserts on the rendered attribute, not merely the absence of a tag.
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "[click me](javascript:alert(1)) and ![img](javascript:alert(1))");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("javascript:", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"\"", body, StringComparison.Ordinal);
        Assert.Contains("src=\"\"", body, StringComparison.Ordinal);
        Assert.Contains("click me", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_link_and_image_destinations_still_work()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync(
            "page.md",
            "[external](https://example.com) [mail](mailto:me@example.com) [other](/wiki/other) [frag](#section)");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("href=\"https://example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:me@example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/wiki/other\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"#section\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_response_carries_a_script_blocking_content_security_policy()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Body.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));

        // §8 block B: enabling InteractiveServer app-wide (D7, D19 §3) makes the framework append its
        // own defensive `frame-ancestors 'self'` header for circuit protection alongside this app's own
        // policy -- both are present rather than one replacing the other (a browser intersects two
        // same-named CSP headers, so this can only narrow what is allowed, never widen it). Asserts the
        // app's own policy specifically, not merely "a" Content-Security-Policy header.
        var csp = Assert.Single(values, value => value.StartsWith("default-src 'self';", StringComparison.Ordinal));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_canonical_route_value_is_refused_even_though_it_would_re_encode_to_a_real_pages_route()
    {
        // S2 (block 3 remediation) — the exact spec fixture (specs/content-store/spec.md, "Only a
        // page's own canonical route serves it"): only "Chapter_1.md" exists, whose canonical route is
        // "Chapter__1". Requesting "/wiki/Chapter%20%201" (two literal spaces once ASP.NET Core routing
        // decodes it) decodes to "Chapter  1.md" and re-encodes to the same "Chapter__1" route — the
        // pre-remediation read path served this file anyway. It must not: the request's own route value
        // is not the received form of "Chapter_1.md"'s canonical route.
        await SeedAccountAsync("alice");
        await WritePageAsync("Chapter_1.md", "Should never be served by the wrong route value.");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/Chapter%20%201");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Should never be served by the wrong route value.", body, StringComparison.Ordinal);

        // The canonical route for the same file still works.
        var canonical = await (await SignInAsync("alice")).GetAsync("/wiki/Chapter__1");
        var canonicalBody = await canonical.Content.ReadAsStringAsync();
        Assert.Contains("Should never be served by the wrong route value.", canonicalBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frontmatter_title_and_git_author_are_html_escaped_not_raw()
    {
        // D13 is a security property: the Markdig pipeline's raw-HTML-off setting must be the *only*
        // path content is exempted from, not one of several. Title and author both come from
        // attacker-influenced sources (frontmatter is push-reachable; a pushed commit's author is
        // self-asserted per D5) and must render through ordinary Razor escaping, not MarkupString.
        await SeedAccountAsync("alice");
        await WritePageAsync(
            "page.md",
            """
            ---
            title: <script>alert('title')</script>
            ---
            Body.
            """,
            authorName: "<script>alert('author')</script>");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert('title')", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert('author')", body, StringComparison.Ordinal);
    }

    private async Task WritePageAsync(
        string relativePath,
        string content,
        string authorName = "Test Author",
        string authorEmail = "author@zerowiki.example")
    {
        // Force the host (and EnsureContentRepositoryAsync) to have started before writing under its
        // data root — GetAsync("/") is anonymous-safe (AD21) and cheap.
        await _app.CreateHttpClient().GetAsync("/");

        var repositoryRoot = Path.Combine(_app.DataRoot, "wiki");
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);

        var author = new GitAuthor(authorName, authorEmail);
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "add " + relativePath], author.ToEnvironmentVariables());
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
