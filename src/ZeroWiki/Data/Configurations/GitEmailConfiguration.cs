using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ZeroWiki.Data.Configurations;

public sealed class GitEmailConfiguration : IEntityTypeConfiguration<GitEmail>
{
    public void Configure(EntityTypeBuilder<GitEmail> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Email)
            .IsRequired()
            .HasMaxLength(320)
            .UseCollation("NOCASE");

        builder.HasIndex(e => e.Email).IsUnique();
    }
}
