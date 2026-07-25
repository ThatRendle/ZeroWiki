using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data.Configurations;
using ZeroWiki.Data.Converters;

namespace ZeroWiki.Data;

/// <summary>
/// The identity store: accounts, git emails, invitations, and git access tokens.
/// Lives in its own SQLite file on the mounted volume, separate from the content
/// git repository — identity is not versioned content.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<GitEmail> GitEmails => Set<GitEmail>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<GitToken> GitTokens => Set<GitToken>();

    /// <summary>
    /// Applied as a convention rather than per property on purpose: it covers
    /// <see cref="DateTimeOffset"/>? as well, and a timestamp column added later cannot be
    /// left out of it by omission.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Iso8601UtcDateTimeOffsetConverter>()
            .HaveMaxLength(Iso8601UtcDateTimeOffsetConverter.FormattedLength);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new GitEmailConfiguration());
        modelBuilder.ApplyConfiguration(new InvitationConfiguration());
        modelBuilder.ApplyConfiguration(new GitTokenConfiguration());
    }
}
