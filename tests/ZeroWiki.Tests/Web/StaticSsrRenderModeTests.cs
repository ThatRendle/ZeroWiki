using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Data;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Task 6.3 — the authentication surface renders as Static SSR and holds no circuit.
/// </summary>
/// <remarks>
/// This is already true of the application as written, which is exactly why it needs pinning: the
/// day somebody adds <c>@rendermode InteractiveServer</c> to a page, every existing test still
/// passes and a login form silently starts holding a SignalR circuit open per anonymous visitor.
/// The two tests below are the condition and the outcome, and both are needed — the first names the
/// mistake at the point it is made, the second still fails if a render mode arrives by some route
/// the first does not model.
/// </remarks>
public sealed class StaticSsrRenderModeTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void No_component_declares_an_interactive_render_mode()
    {
        // The condition. `@rendermode X` on a component compiles to a RenderModeAttribute on its
        // class, so this catches the declaration itself rather than one of its symptoms, and names
        // the offending component when it fails.
        var interactive = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttribute<RenderModeAttribute>(inherit: false) is not null)
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(interactive);
    }

    [Fact]
    public async Task The_interactive_blazor_endpoint_is_not_mapped()
    {
        // The outcome. Without an interactive render mode registered there is no /_blazor hub, so
        // no page can hold a circuit however it is annotated. Asked as a signed-in member because
        // an anonymous request to any unmapped URL is answered by the landing page (AD21), which
        // would make the status assertion pass whether the hub existed or not.
        await SeedAccountAsync();
        var client = await SignInAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_blazor")).StatusCode);

        var routes = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .OfType<string>()
            .Where(pattern => pattern.Contains("_blazor", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(routes);
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
