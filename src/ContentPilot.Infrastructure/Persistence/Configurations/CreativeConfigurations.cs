using ContentPilot.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        builder.ToTable("template_versions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TemplateId).HasMaxLength(80).IsRequired();
        builder.Property(t => t.ManifestJson).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(t => t.FetchedAt).IsRequired();

        // One row per (template, version) — a re-fetch of an unchanged manifest reuses it
        // rather than growing the table, the same pattern BrandProfileVersion uses.
        builder.HasIndex(t => new { t.TemplateId, t.Version }).IsUnique();
    }
}

public sealed class CreativeSpecConfiguration : IEntityTypeConfiguration<CreativeSpec>
{
    public void Configure(EntityTypeBuilder<CreativeSpec> builder)
    {
        builder.ToTable("creative_specs");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.SpecJson).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.SpecHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne<ContentItem>()
            .WithMany().HasForeignKey(s => s.ContentItemId).OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TemplateVersion>()
            .WithMany().HasForeignKey(s => s.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);

        // One immutable spec per attempt.
        builder.HasIndex(s => new { s.ContentItemId, s.Attempt }).IsUnique();
    }
}

public sealed class ContentAssetConfiguration : IEntityTypeConfiguration<ContentAsset>
{
    public void Configure(EntityTypeBuilder<ContentAsset> builder)
    {
        builder.ToTable("content_assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.Kind).HasConversion<short>().IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(a => a.MediaType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.MetaJson).HasColumnType("jsonb");
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasOne<ContentItem>()
            .WithMany().HasForeignKey(a => a.ContentItemId).OnDelete(DeleteBehavior.Cascade);

        // A carousel or reel can have several assets per attempt (slides, a cover), so
        // this is not unique — only (ContentItemId, Attempt) as a lookup, plus Kind to
        // narrow it. The review UI's query: this item, this attempt, all its pieces.
        builder.HasIndex(a => new { a.ContentItemId, a.Attempt });
    }
}
