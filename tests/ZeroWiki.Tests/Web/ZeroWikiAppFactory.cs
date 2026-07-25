using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroWiki.Data;

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
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"zerowiki-web-{Guid.NewGuid():n}.db");

    /// <summary>A client that surfaces redirects instead of following them.</summary>
    public HttpClient CreateHttpClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<IReadOnlyList<Account>> GetAccountsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        return await db.Accounts.AsNoTracking().ToListAsync();
    }

    /// <remarks>
    /// The environment is pinned rather than inherited: <c>Program.cs</c> branches on it for the
    /// exception handler and HSTS, so leaving it to whatever the host machine exports would make
    /// these tests exercise a pipeline nobody chose. <c>Production</c> is the shape the container
    /// actually ships in.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment(Environments.Production)
        .UseSetting("ConnectionStrings:IdentityDb", $"Data Source={_databasePath}");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            File.Delete(path);
        }
    }
}
