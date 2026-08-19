using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Components.Pages;
using ZeroWiki.Content;
using ZeroWiki.Data;
using ZeroWiki.Identity;
using ZeroWiki.Security;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// §12 (<c>12.2</c>, second pass) — the falsifier for the round-trip a live push depends on: an
/// <c>InteractiveServer</c> render of <see cref="ChangedOnDiskIndicator"/> must receive, on the
/// interactive side of the SSR→circuit boundary, the same route its call site passed on the SSR side.
/// Before <c>12.3</c>'s first pass this was false — the live diagnostic run recorded in
/// <c>DEVLOG.md</c> under <c>## 12.</c> found the interactive instance subscribing under
/// <c>default(EncodedRoute)</c>. The fix that made this pass changed shape between passes: the first
/// gave <see cref="EncodedRoute"/> its own <c>System.Text.Json</c> converter, which a reviewer showed
/// widens who can construct the type (an external caller with no <c>InternalsVisibleTo</c> grant could
/// deserialize a populated instance); the second, current one instead has
/// <see cref="ChangedOnDiskIndicator.Route"/> cross the boundary as a plain <see cref="string"/>, which
/// the component reconstructs into an <see cref="EncodedRoute"/> itself, in-assembly. This test's own
/// assertion type changed with it (<see cref="string"/>, not <see cref="EncodedRoute"/>) but the
/// falsifier it states is unchanged: the value observable on the interactive side of the boundary
/// equals the value the call site passed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why bUnit cannot be this test.</b> bUnit assigns component parameters directly to an in-process
/// instance — it never serializes them across the SSR→circuit boundary, which is exactly the crossing
/// this test exercises.
/// </para>
/// <para>
/// <b>The instrument.</b> <see cref="InteractiveComponentSurfaceTests"/> already establishes that a
/// wiki page's response carries a matched marker pair — <c>&lt;!--Blazor:{...}--&gt;</c> — for exactly
/// one <c>InteractiveServer</c> component instance, and that the open marker's JSON carries a
/// <c>descriptor</c> field. That field is the DataProtection-protected payload the framework's own
/// <c>IServerComponentDeserializer</c> (<c>Microsoft.AspNetCore.Components.Server</c>, internal to that
/// assembly) unprotects and parses when a real circuit starts — the same code path a real browser
/// drives, reached here without one. This test resolves that service from the running host's own DI
/// container (so it uses the host's actual DataProtection key ring) and calls the exact method the
/// framework calls, <c>TryDeserializeComponentDescriptorCollection</c>, reflectively — the type is
/// <c>internal</c>, so there is no compile-time reference to bind to. This shape (test-only reflection
/// over a framework-internal deserializer, resolved from a live host) was reached by dumping a real
/// marker and its parameter payload against this app's own real DI container, not by assumption or by
/// reading the framework's documentation — see the DEVLOG post this test accompanies for the raw dump.
/// </para>
/// <para>
/// <b>The call shape, established by execution rather than by the framework's source:</b>
/// <c>TryDeserializeComponentDescriptorCollection</c> does not take a single marker's
/// <c>descriptor</c> string — it takes a JSON array of whole marker objects, mirroring what
/// <c>blazor.server.js</c> actually sends when a circuit starts (each entry re-protected internally).
/// Passing the bare <c>descriptor</c> string alone throws a <c>JsonException</c> at the very first
/// token; passing <c>["&lt;descriptor&gt;"]</c> throws deserializing element 0 as a
/// <c>ComponentMarker</c>; passing <c>[&lt;the whole marker object&gt;]</c> succeeds and returns a
/// <c>ComponentDescriptor</c> whose <c>Parameters</c> is a real <c>ParameterView</c> carrying a plain
/// <see cref="string"/> for <c>Route</c> — confirming the deserializer reconstructs exactly the type
/// the component parameter now declares, nothing more exotic.
/// </para>
/// </remarks>
public sealed class ChangedOnDiskIndicatorRouteRoundTripTests : IDisposable
{
    private const string Username = "alice";
    private const string Password = "a good long passphrase";

    private readonly ZeroWikiAppFactory _app = new();
    private readonly GitProcessRunner _git = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_interactive_instance_receives_the_route_its_call_site_passed()
    {
        await SeedAccountAsync();
        await WritePageAsync("page.md", "Body.");
        var client = await SignInAsync();

        var body = await (await client.GetAsync("/wiki/page")).Content.ReadAsStringAsync();

        var route = InteractiveComponentMarkerParameters.GetParameterValue<string>(
            _app, body, typeof(ChangedOnDiskIndicator), "Route");

        // The SSR side passed the page's own canonical route's raw value,
        // `PageRouteCodec.Encode("page.md").Value` (WikiPage.razor:
        // `<ChangedOnDiskIndicator Route="@notifiedPage.Route.Value" />`), which for this unencoded
        // filename is the literal string "page". Asserting on the value itself, not merely that
        // something round-tripped, is what keeps this the falsifier the brief asked for.
        Assert.Equal("page", route);
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
        await _git.RunOrThrowAsync(repositoryRoot, ["commit", "-m", "add " + relativePath], author.ToEnvironmentVariables());
    }

    private async Task SeedAccountAsync() =>
        await _app.WithDbAsync(async db =>
        {
            db.Accounts.Add(new ZeroWiki.Data.Account
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
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions
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
}
