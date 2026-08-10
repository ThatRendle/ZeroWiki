using System.Net;
using System.Reflection;
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
/// exactly, rather than deleting it, keeps the tripwire's real point intact: the day some other
/// component (especially one on the authentication surface below) gains
/// <c>@rendermode InteractiveServer</c>, this still fails and names the offender, the same as before
/// §8. <see cref="ZeroWiki.Components.Pages.ChangedOnDiskIndicator"/> only ever mounts inside
/// <c>WikiPage.razor</c>'s rendered-body branch (never on an anonymous, login, or invitation
/// surface), so the second test below still holds unchanged: a signed-in member's own authenticated
/// pages other than a wiki page in view still establish no circuit, and the endpoint is asserted
/// mapped now rather than absent, since D19 §3 rides the framework's own hub rather than a bespoke
/// one.
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
