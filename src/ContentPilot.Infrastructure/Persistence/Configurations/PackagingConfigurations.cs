using ContentPilot.Domain.Content;
using ContentPilot.Domain.Packaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class CampaignPackageConfiguration : IEntityTypeConfiguration<CampaignPackage>
{
    public void Configure(EntityTypeBuilder<CampaignPackage> builder)
    {
        builder.ToTable("campaign_packages");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.ManifestJson).HasColumnType("jsonb").IsRequired();
        builder.Property(p => p.ZipKey).HasMaxLength(500);
        builder.Property(p => p.BuiltAt).IsRequired();

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(p => p.CampaignId).OnDelete(DeleteBehavior.Cascade);

        // One package per campaign: a rebuild updates this row in place.
        builder.HasIndex(p => p.CampaignId).IsUnique();
    }
}

public sealed class HumanRatingConfiguration : IEntityTypeConfiguration<HumanRating>
{
    public void Configure(EntityTypeBuilder<HumanRating> builder)
    {
        builder.ToTable("human_ratings");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Score).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(1000);
        builder.Property(r => r.RatedAt).IsRequired();

        builder.HasOne<ContentItem>()
            .WithMany().HasForeignKey(r => r.ContentItemId).OnDelete(DeleteBehavior.Cascade);

        // One rating per item: a second click replaces the first.
        builder.HasIndex(r => r.ContentItemId).IsUnique();
    }
}
