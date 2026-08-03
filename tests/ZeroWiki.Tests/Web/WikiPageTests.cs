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
    public async Task Authorship_is_read_from_git_history()
    {
        await SeedAccountAsync("alice");
        await WritePageAsync("page.md", "Body.", authorName: "Some Author", authorEmail: "author@zerowiki.example");

        var response = await (await SignInAsync("alice")).GetAsync("/wiki/page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Some Author", body, StringComparison.Ordinal);
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
