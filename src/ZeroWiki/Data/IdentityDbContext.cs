using Microsoft.EntityFrameworkCore;
using ZeroWiki.Data.Configurations;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new GitEmailConfiguration());
        modelBuilder.ApplyConfiguration(new InvitationConfiguration());
        modelBuilder.ApplyConfiguration(new GitTokenConfiguration());
    }
}
