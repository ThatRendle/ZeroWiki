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

    private readonly string _databasePath;
    private readonly string _dataRoot;
    private readonly string _connectionString;
    private readonly bool _useRealServer;

    /// <summary>Everything this application logged, so a test can sweep it for a secret.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The SQLite file backing this instance — reusable by <see cref="RestartedFrom"/>.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// The <c>ContentStorage:DataRoot</c> this instance was configured with, hence where its
    /// DataProtection key ring lives on disk — reusable by <see cref="RestartedFrom"/>.
    /// </summary>
    public string DataRoot => _dataRoot;

    public ZeroWikiAppFactory()
        : this(
            Path.Combine(Path.GetTempPath(), $"zerowiki-web-{Guid.NewGuid():n}.db"),
            Path.Combine(Path.GetTempPath(), $"zerowiki-web-data-{Guid.NewGuid():n}"))
    {
    }

    private ZeroWikiAppFactory(string databasePath, string dataRoot, bool useRealServer = false)
    {
        _databasePath = databasePath;
        _dataRoot = dataRoot;
        _connectionString = TestDatabase.ConnectionStringFor(_databasePath);
        _useRealServer = useRealServer;

        if (_useRealServer)
        {
            // Must be called before this instance is first started (Services/CreateClient/etc.) --
            // WebApplicationFactory throws InvalidOperationException otherwise. Port 0: dynamic
            // selection, read back afterward via RealServerAddress.
            UseKestrel(port: 0);
        }
    }

    /// <summary>
    /// Builds a fresh host — a new DI container, a new in-process DataProtection key ring loaded
    /// from disk — against the <em>same</em> database file and <em>same</em> key-ring directory as
    /// <paramref name="previous"/>. Everything a real process restart changes (the DI container,
    /// every in-memory singleton, the loaded key ring) changes here too; only the OS process
    /// identity doesn't, which nothing under test depends on. Used to prove a cookie issued before
    /// a restart is still valid after one — see <c>LoginPageTests</c>.
    /// </summary>
    public static ZeroWikiAppFactory RestartedFrom(ZeroWikiAppFactory previous) =>
        new(previous.DatabasePath, previous.DataRoot);

    /// <summary>
    /// Task 7.3/7.4: boots the real application on a real listening Kestrel socket rather than the
    /// in-memory <c>TestServer</c> every other test uses — the one property a real <c>git</c> client
    /// needs, since it is a separate OS process that must dial an actual port to clone, fetch or
    /// push. See <see cref="RealServerAddress"/> for the address it bound to.
    /// </summary>
    /// <param name="dataRoot">
    /// When supplied, this instance's <c>ContentStorage:DataRoot</c> — pre-populate it with an
    /// existing git repository (e.g. on a branch other than <c>main</c>) before constructing this
    /// factory to exercise startup's adopted-repository path rather than first-ever <c>git init</c>.
    /// Left <see langword="null"/> for a fresh, empty root exactly like the parameterless
    /// constructor's.
    /// </param>
    public static ZeroWikiAppFactory WithRealServer(string? dataRoot = null) =>
        new(
            Path.Combine(Path.GetTempPath(), $"zerowiki-web-{Guid.NewGuid():n}.db"),
            dataRoot ?? Path.Combine(Path.GetTempPath(), $"zerowiki-web-data-{Guid.NewGuid():n}"),
            useRealServer: true);

    /// <summary>
    /// The real address Kestrel bound to — only meaningful when constructed via
    /// <see cref="WithRealServer"/>. Accessing this triggers server startup (mirrors
    /// <see cref="Services"/>'s own behaviour), so no separate "start" call is needed first.
    /// </summary>
    public Uri RealServerAddress
    {
        get
        {
            if (!_useRealServer)
            {
                throw new InvalidOperationException(
                    $"{nameof(RealServerAddress)} is only meaningful on an instance constructed via " +
                    $"{nameof(WithRealServer)}.");
            }

            // Services touches StartServer() the same way the base class's own Server property
            // does, which is what makes ClientOptions.BaseAddress reflect the port Kestrel actually
            // bound rather than the pre-startup placeholder.
            _ = Services;
            return ClientOptions.BaseAddress;
        }
    }

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
    /// <para>
    /// <c>ContentStorage:DataRoot</c> is pinned to a throwaway temp directory for the same reason
    /// as the identity connection string above: left at its <c>/data</c> default, the DataProtection
    /// key ring (<c>Program.cs</c>) would try to create <c>/data/keys</c> on whatever host runs the
    /// test suite, which has no such directory and no permission to make one.
    /// </para>
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment(Environments.Production)
        .UseSetting("ConnectionStrings:IdentityDb", _connectionString)
        .UseSetting("ContentStorage:DataRoot", _dataRoot)
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

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
