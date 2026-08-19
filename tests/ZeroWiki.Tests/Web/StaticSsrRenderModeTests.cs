using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 6.3 / §8 block B — the authentication surface renders as Static SSR and holds no circuit;
/// <c>ChangedOnDiskIndicator</c> is the one deliberate exception (D7, D19 §3), not an opening for
/// anything else.
/// </summary>
/// <remarks>
/// Originally pinned "no component anywhere declares an interactive render mode" — true only until
/// §8 block B wired D7's own planned exception in. Narrowing the pin to name that one component
/// exactly, rather than deleting it, keeps the tripwire's real point intact for the one axis it can
/// see: the day some other component gains its <em>own</em> <c>@rendermode InteractiveServer</c>
/// declaration, this still fails and names the offender.
/// </remarks>
/// <remarks>
/// <b>§8 remediation (supervisor finding): this test has a blind axis, corrected here rather than
/// left implicit.</b> <c>GetCustomAttribute&lt;RenderModeAttribute&gt;</c> only ever sees a
/// <em>class-level</em> declaration — <c>@rendermode InteractiveServer</c> written inside a
/// component's own file. The call-site form — <c>&lt;SomeComponent @rendermode="InteractiveServer" /&gt;</c>
/// written inside a <em>caller</em>, e.g. <c>Login.razor</c> — emits no such attribute on any class
/// and is invisible to this test; before §8 that did not matter, because with no interactive render
/// mode registered at all there was no <c>/_blazor</c> hub for either form to reach. §8 both made
/// that hazard reachable (registering <c>InteractiveServer</c> app-wide) and inverted the only test
/// that forbade it — the earlier revision of this docstring claimed the second test below still
/// covered it, which was false; it asserted only that the hub exists, nothing about who may hold a
/// circuit. <see cref="InteractiveComponentSurfaceTests"/> is the actual backstop for this axis now:
/// it is outcome-based (counts the marker every interactive component instance renders, not a
/// class-level C# attribute), so it is sensitive to <em>either</em> form of <c>@rendermode</c> and to
/// <c>ChangedOnDiskIndicator</c> actually being mounted, on every authentication-surface page it
/// checks.
/// </remarks>
public sealed class StaticSsrRenderModeTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Only_the_changed_on_disk_indicator_declares_an_interactive_render_mode()
    {
        // `@rendermode X` on a component compiles to a RenderModeAttribute on its class, so this
        // catches the declaration itself rather than one of its symptoms, and names every offending
        // component (not just whether one exists) when it fails.
        var interactive = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttribute<RenderModeAttribute>(inherit: false) is not null)
            .Select(type => type.FullName)
            .ToList();

        Assert.Equal([typeof(ZeroWiki.Components.Pages.ChangedOnDiskIndicator).FullName], interactive);
    }

    [Fact]
    public async Task The_interactive_blazor_endpoint_is_mapped_for_the_one_component_that_needs_it()
    {
        // §8 block B (D19 §3): D7's plan was always for a component to eventually need this, and now
        // one does, so the hub is legitimately mapped app-wide -- there is no way to scope a single
        // SignalR hub to only the pages that use it. Asked as a signed-in member because an
        // anonymous request to any unmapped URL is answered by the landing page (AD21), which would
        // make a bare status assertion pass whether the hub existed or not.
        await SeedAccountAsync();
        var client = await SignInAsync();

        Assert.NotEqual(HttpStatusCode.NotFound, (await client.GetAsync("/_blazor")).StatusCode);

        var routes = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .Where(pattern => pattern.Contains("_blazor", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(routes);
    }

    [Fact]
    public void None_of_the_mapped_blazor_hub_family_is_anonymously_exempt()
    {
        // §8 remediation (supervisor finding): the earlier evidence for this posture was one
        // anonymous `GET /_blazor`, which cannot see /_blazor/negotiate, /_blazor/disconnect/, or
        // /_blazor/initializers/ -- "sample one path and reason about the rest" is the exact shape
        // §7 shipped twice. Reads AnonymousGate's own instrument (IAllowAnonymous endpoint metadata)
        // for the whole mapped family instead of sampling a request against one of them.
        //
        // Measured directly (not assumed): the real family this app maps is exactly these four routes
        // -- confirmed by enumerating EndpointDataSource against a running instance. A future ASP.NET
        // Core version changing that shape should fail this assertion loudly, not pass silently having
        // never checked the new member.
        var blazorHubEndpoints = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? string.Empty)
                .StartsWith("/_blazor", StringComparison.Ordinal))
            .ToList();

        var patterns = blazorHubEndpoints
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .OrderBy(pattern => pattern, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "/_blazor", "/_blazor/disconnect/", "/_blazor/initializers/", "/_blazor/negotiate" }
                .OrderBy(pattern => pattern, StringComparer.Ordinal),
            patterns);

        foreach (var endpoint in blazorHubEndpoints)
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }
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

    private async Task<HttpClient> SignInAsync()
    {
        var client = _app.CreateHttpClient();
        var fields = await StaticSsrForm.GetHiddenFieldsAsync(client, "/login");

        HttpAssertions.AssertRedirectedTo("/", await StaticSsrForm.PostAsync(client, "/login", fields.Concat(
        [
            KeyValuePair.Create("Input.Username", Username),
            KeyValuePair.Create("Input.Password", Password),
        ])));

        return client;
    }
}
