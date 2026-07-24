using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ZeroWiki.Data;

public static class IdentityDbStartupExtensions
{
    /// <summary>
    /// Registers <see cref="IdentityDbContext"/> against the <c>IdentityDb</c> connection
    /// string, creating the containing directory on the mounted volume if needed. The
    /// identity store is a single SQLite file, always separate from the content git repo.
    /// </summary>
    public static IServiceCollection AddIdentityDb(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("IdentityDb")
            ?? throw new InvalidOperationException(
                "Missing required connection string 'ConnectionStrings:IdentityDb'.");

        EnsureDataDirectoryExists(connectionString);

        services.AddDbContext<IdentityDbContext>(options => options.UseSqlite(connectionString));

        return services;
    }

    /// <summary>Applies pending EF Core migrations, creating the database file on first run.</summary>
    public static async Task MigrateIdentityDbAsync(this IHost app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static void EnsureDataDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
