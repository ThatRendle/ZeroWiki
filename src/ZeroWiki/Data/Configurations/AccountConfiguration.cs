using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ZeroWiki.Data.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Username)
            .IsRequired()
            .HasMaxLength(64)
            .UseCollation("NOCASE");

        builder.HasIndex(a => a.Username).IsUnique();

        builder.Property(a => a.PasswordHash).IsRequired();

        builder.Property(a => a.DisplayName)
            .IsRequired()
            .HasMaxLength(128);

        builder.HasMany(a => a.GitEmails)
            .WithOne(e => e.Account)
            .HasForeignKey(e => e.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.GitTokens)
            .WithOne(t => t.Account)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
