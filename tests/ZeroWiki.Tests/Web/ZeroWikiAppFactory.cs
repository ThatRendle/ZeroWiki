using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroWiki.Data;
using ZeroWiki.Tests.Identity;

namespace ZeroWiki.Tests.Web;

/// <summary>
/// Boots the real application — the real pipeline, antiforgery, routing and Static SSR rendering
/// — against a throwaway SQLite file, so a page can be exercised the way a browser does.
/// </summary>
/// <remarks>
/// Unit tests call services directly and cannot see whether a form actually works: a form whose
/// field names do not match its binder posts nothing, returns 200, and leaves every unit test
/// green. Anything reached through a Static SSR form needs a test at this level.
/// </remarks>
public sealed class ZeroWikiAppFactory : WebApplicationFactory<Program>
{
    /// <summary>The origin every test client addresses; see <see cref="CreateHttpClient"/>.</summary>
    public static readonly Uri BaseAddress = new("https://localhost");

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"zerowiki-web-{Guid.NewGuid():n}.db");

    private readonly string _connectionString;

    /// <summary>Everything this application logged, so a test can sweep it for a secret.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public ZeroWikiAppFactory() => _connectionString = TestDatabase.ConnectionStringFor(_databasePath);

    /// <summary>A client that surfaces redirects instead of following them.</summary>
    /// <remarks>
    /// Addressed over HTTPS because the pinned <c>Production</c> environment marks the
    /// authentication cookie <c>Secure</c>: over plain HTTP the client would accept the sign-in
    /// response and then never send the cookie back, so every authenticated test would fail in a
    /// way that looks like a broken login. This exercises the shipped cookie policy rather than
    /// working around it.
    /// </remarks>
    public HttpClient CreateHttpClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = BaseAddress,
        });

    /// <summary>Runs <paramref name="action"/> against the running application's own store.</summary>
    public async Task<T> WithDbAsync<T>(Func<IdentityDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
    }

    public async Task WithDbAsync(Func<IdentityDbContext, Task> action) =>
        await WithDbAsync<object?>(async db =>
        {
            await action(db);
            return null;
        });

    public async Task<IReadOnlyList<Account>> GetAccountsAsync() =>
        await WithDbAsync(db => db.Accounts.AsNoTracking().ToListAsync());

    /// <remarks>
    /// The environment is pinned rather than inherited: <c>Program.cs</c> branches on it for the
    /// exception handler and HSTS, so leaving it to whatever the host machine exports would make
    /// these tests exercise a pipeline nobody chose. <c>Production</c> is the shape the container
    /// actually ships in.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment(Environments.Production)
        .UseSetting("ConnectionStrings:IdentityDb", _connectionString)
        .ConfigureLogging(logging => logging
            .AddProvider(Logs)

            // A provider-specific rule, so it outranks appsettings' "Microsoft.AspNetCore":
            // "Warning" for this sink alone and changes what no other provider sees. Without it the
            // request log sits below the threshold and never reaches the capture — and the request
            // log is the entry that carries the URL, which is where a secret nobody meant to write
            // down would turn up.
            .AddFilter<CapturingLoggerProvider>(category: null, level: LogLevel.Trace));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // Safe without clearing any connection pool: TestDatabase turns pooling off, so disposing
        // the host above closed every handle to this file.
        TestDatabase.Delete(_databasePath);
    }
}
