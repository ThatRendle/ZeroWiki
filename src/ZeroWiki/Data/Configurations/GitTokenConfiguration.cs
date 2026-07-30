using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ZeroWiki.Data.Configurations;

public sealed class GitTokenConfiguration : IEntityTypeConfiguration<GitToken>
{
    public void Configure(EntityTypeBuilder<GitToken> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
    }
}
