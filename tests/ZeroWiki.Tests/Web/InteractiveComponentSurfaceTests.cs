using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// §8 remediation (supervisor findings 1 and 3, over `e4e5022..f985c5e`): closes both with one
/// mechanism-independent assertion — a wiki page's response carries markers for exactly one
/// <c>InteractiveServer</c> component instance, and every other surface carries none. Unlike
/// <see cref="StaticSsrRenderModeTests.Only_the_changed_on_disk_indicator_declares_an_interactive_render_mode"/>,
/// which only ever sees a <em>class-level</em> <c>RenderModeAttribute</c> (the form
/// <c>@rendermode InteractiveServer</c> written inside a component's own file), this tests the
/// <em>outcome</em>: it is sensitive to <c>ChangedOnDiskIndicator</c> actually being mounted in
/// <c>WikiPage.razor</c> (delete that usage and the wiki-page count drops to zero, unlike the
/// class-level test, which keeps passing because the class still carries its own attribute) and to the
/// call-site <c>@rendermode="X"</c> form on any other component, since both forms produce the identical
/// marker at render time regardless of which mechanism produced them.
/// </summary>
public sealed class InteractiveComponentSurfaceTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly GitProcessRunner _git = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_wiki_page_carries_markers_for_exactly_one_interactive_component_instance()
    {
        // Each InteractiveServer component instance the framework prerenders emits a matched pair --
        // an open marker carrying the full descriptor and a close marker carrying only the same
        // prerenderId (confirmed by execution, not assumed: dumping the real markers off a real
        // response showed exactly this shape) -- so "exactly one component" is two markers, not one.
        await SeedAccountAsync();
        await WritePageAsync("page.md", "Body.");
        var client = await SignInAsync();

        var body = await (await client.GetAsync("/wiki/page")).Content.ReadAsStringAsync();

        Assert.Equal(2, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    [Fact]
    public async Task Rendering_a_wiki_page_over_a_plain_http_client_never_subscribes_to_the_notifier()
    {
        // §8 remediation, finding 1's second half: RendererInfo.IsInteractive is false for a Static
        // SSR prerender pass, and this whole test suite -- like StaticSsrRenderModeTests before it --
        // never drives a real browser/SignalR client, so no test request ever produces a second,
        // genuinely interactive component instance either. The ONE thing standing between an ordinary
        // page view (this test's own shape, and every other test in this suite) and a subscription
        // leaked into the process-lifetime PageChangeNotifier singleton is ChangedOnDiskIndicator's
        // own `if (RendererInfo.IsInteractive)` guard. Substitutes a spy notifier via DI rather than
        // asserting on PageChangeNotifier's internals, which expose no subscription count on purpose.
        await SeedAccountAsync();
        await WritePageAsync("page.md", "Body.");

        var spy = new SpyPageChangeNotifier();
        using var spyFactory = _app.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IPageChangeNotifier>(spy)));

        var client = await SignInOnAsync(spyFactory);
        await client.GetAsync("/wiki/page");

        Assert.Equal(0, spy.SubscribeCallCount);
    }

    [Fact]
    public async Task The_login_page_carries_no_interactive_component_marker()
    {
        var body = await _app.CreateHttpClient().GetStringAsync("/login");

        Assert.Equal(0, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    [Fact]
    public async Task The_anonymous_landing_page_carries_no_interactive_component_marker()
    {
        var body = await _app.CreateHttpClient().GetStringAsync("/");

        Assert.Equal(0, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    [Fact]
    public async Task The_invitation_redemption_page_carries_no_interactive_component_marker()
    {
        // A malformed/unmatched token still renders RedeemInvitation.razor's own "not valid" body
        // (RedeemInvitationPageTests' own precedent) -- no real invitation needs issuing to prove this
        // page's render mode.
        var body = await _app.CreateHttpClient().GetStringAsync($"{InvitationPolicy.RedemptionPath}/not-a-real-token");

        Assert.Equal(0, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    [Fact]
    public async Task The_account_page_carries_no_interactive_component_marker()
    {
        await SeedAccountAsync();
        var client = await SignInAsync();

        var body = await client.GetStringAsync("/account");

        Assert.Equal(0, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    [Fact]
    public async Task The_invitations_page_carries_no_interactive_component_marker()
    {
        await SeedAccountAsync();
        var client = await SignInAsync();

        var body = await client.GetStringAsync("/invitations");

        Assert.Equal(0, HttpAssertions.CountInteractiveComponentMarkers(body));
    }

    private async Task WritePageAsync(string relativePath, string content)
    {
        // Force the host (and EnsureContentRepositoryAsync) to have started before writing under its
        // data root -- GetAsync("/") is anonymous-safe (AD21) and cheap.
        await _app.CreateHttpClient().GetAsync("/");

        var repositoryRoot = Path.Combine(_app.DataRoot, "wiki");
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);

        var author = new GitAuthor("Test Author", "author@zerowiki.example");
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "add " + relativePath], author.ToEnvironmentVariables());
    }

    private async Task SeedAccountAsync() =>
        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = Guid.NewGuid(),
                Username = Username,
                PasswordHash = new Argon2idPasswordHasher().Hash(Password),
                DisplayName = Username,
                CreatedAt = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        });

    private async Task<HttpClient> SignInAsync() => await SignInOnAsync(_app);

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/> returns a base
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> (a <c>DelegatedWebApplicationFactory</c>
    /// wrapper at runtime, confirmed by execution — not the derived <see cref="ZeroWikiAppFactory"/>),
    /// so this mirrors <see cref="ZeroWikiAppFactory.CreateHttpClient"/>'s own client options directly
    /// rather than needing that method.
    /// </summary>
    private static async Task<HttpClient> SignInOnAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = ZeroWikiAppFactory.BaseAddress,
        });
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/login");

        HttpAssertions.AssertRedirectedTo("/", await StaticSsrForm.PostAsync(client, "/login", fields.Concat(
        [
            KeyValuePair.Create("Input.Username", Username),
            KeyValuePair.Create("Input.Password", Password),
        ])));

        return client;
    }

    /// <summary>Records every <see cref="Subscribe"/> call and nothing else -- never invokes a
    /// registered callback, since this test only needs to know whether a subscription attempt was
    /// ever made, not what happens after one.</summary>
    private sealed class SpyPageChangeNotifier : IPageChangeNotifier
    {
        private int _subscribeCallCount;

        public int SubscribeCallCount => _subscribeCallCount;

        public IDisposable Subscribe(EncodedRoute route, Func<Task> onChanged)
        {
            Interlocked.Increment(ref _subscribeCallCount);
            return new NoopSubscription();
        }

        public Task<PageChangeNotificationResult> NotifyChangedAsync(
            IReadOnlyCollection<EncodedRoute> routes, CancellationToken cancellationToken) =>
            Task.FromResult(new PageChangeNotificationResult(0, 0, [], null));

        private sealed class NoopSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
