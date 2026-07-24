using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ZeroWiki.Data.Configurations;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.TokenHash).IsRequired();
        builder.HasIndex(i => i.TokenHash).IsUnique();

        builder.HasOne(i => i.IssuerAccount)
            .WithMany()
            .HasForeignKey(i => i.IssuerAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
