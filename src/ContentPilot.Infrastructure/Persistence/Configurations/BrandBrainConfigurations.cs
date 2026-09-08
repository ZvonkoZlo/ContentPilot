using System.Text.Json;
using ContentPilot.Domain.Branding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// The split the plan calls for, made concrete: open-ended identity travels as JSONB, and
/// anything cited, counted or filtered on gets real columns.
/// </summary>
public sealed class BrandProfileConfiguration : IEntityTypeConfiguration<BrandProfile>
{
    public void Configure(EntityTypeBuilder<BrandProfile> builder)
    {
        builder.ToTable("brand_profiles");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.OperatorNotes).HasMaxLength(1000);
        builder.Property(p => p.CreatedAt).IsRequired();

        // Read wholesale by the renderer and the prompt builder, never joined or filtered.
        builder.Property(p => p.Visual).HasJsonb(VisualIdentity.Fallback);
        builder.Property(p => p.Voice).HasJsonb(ToneOfVoice.Fallback);
        builder.Property(p => p.Messaging).HasJsonb(Messaging.Fallback);

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(p => p.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        // One profile per brand: a second would make "the brand's identity" ambiguous.
        builder.HasIndex(p => p.BrandId).IsUnique();
    }
}

public sealed class BrandProfileVersionConfiguration : IEntityTypeConfiguration<BrandProfileVersion>
{
    public void Configure(EntityTypeBuilder<BrandProfileVersion> builder)
    {
        builder.ToTable("brand_profile_versions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.TenantId).IsRequired();
        builder.Property(v => v.SnapshotJson).HasColumnType("jsonb").IsRequired();
        builder.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(v => v.CreatedAt).IsRequired();

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(v => v.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        // An unchanged brand reuses its version rather than accumulating identical rows.
        builder.HasIndex(v => new { v.BrandId, v.ContentHash }).IsUnique();
        builder.HasIndex(v => new { v.TenantId, v.BrandId, v.CreatedAt });
    }
}

public sealed class BrandAssetConfiguration : IEntityTypeConfiguration<BrandAsset>
{
    public void Configure(EntityTypeBuilder<BrandAsset> builder)
    {
        builder.ToTable("brand_assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.Kind).HasConversion<short>().IsRequired();
        builder.Property(a => a.Origin).HasConversion<short>().IsRequired();
        builder.Property(a => a.FileName).HasMaxLength(300).IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(a => a.MediaType).HasMaxLength(120).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        // ImageMagick encodes a perceptual hash as image moments across several colour
        // spaces and channels, which runs to hundreds of characters. It is compared for
        // equality and never sorted, so an unbounded text column is the honest choice.
        builder.Property(a => a.PerceptualHash);
        builder.Property(a => a.Variant).HasMaxLength(60);
        builder.Property(a => a.Description).HasMaxLength(500);
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.Property<List<string>>("_tags")
            .HasColumnName("tags").HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();

        builder.Property<List<string>>("_dominantColors")
            .HasColumnName("dominant_colors").HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();

        builder.Ignore(a => a.Tags);
        builder.Ignore(a => a.DominantColors);
        builder.Ignore(a => a.AspectRatio);

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(a => a.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<BrandAsset>()
            .WithMany()
            .HasForeignKey(a => a.ParentAssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // The library view: originals of one kind for one brand.
        builder.HasIndex(a => new { a.TenantId, a.BrandId, a.Kind });

        // Content addressing: the same bytes uploaded twice resolve to one stored object.
        builder.HasIndex(a => new { a.BrandId, a.Sha256, a.Variant })
            .IsUnique()
            .HasDatabaseName("ix_brand_assets_content");
    }
}

public sealed class ProductFactConfiguration : IEntityTypeConfiguration<ProductFact>
{
    public void Configure(EntityTypeBuilder<ProductFact> builder)
    {
        builder.ToTable("product_facts");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.Key).HasMaxLength(80).IsRequired();
        builder.Property(f => f.Statement).HasMaxLength(600).IsRequired();
        builder.Property(f => f.Category).HasConversion<short>().IsRequired();
        builder.Property(f => f.Evidence).HasMaxLength(500);
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(f => f.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        // Copy cites this key. Two facts sharing one would make a citation ambiguous, and
        // the whole claim-grounding check rests on it resolving to exactly one statement.
        builder.HasIndex(f => new { f.BrandId, f.Key }).IsUnique();
    }
}

public sealed class AudiencePersonaConfiguration : IEntityTypeConfiguration<AudiencePersona>
{
    public void Configure(EntityTypeBuilder<AudiencePersona> builder)
    {
        builder.ToTable("audience_personas");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(120).IsRequired();
        builder.Property(p => p.Segment).HasMaxLength(160).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.Detail).HasJsonb(new PersonaDetail());

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(p => p.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.TenantId, p.BrandId, p.IsPrimary });
    }
}

public sealed class ContentPreferencesConfiguration : IEntityTypeConfiguration<ContentPreferences>
{
    public void Configure(EntityTypeBuilder<ContentPreferences> builder)
    {
        builder.ToTable("content_preferences");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.GenerationDay).HasConversion<short>().IsRequired();
        builder.Property(p => p.GenerationTime).IsRequired();

        builder.Property<List<string>>("_excludedTopics")
            .HasColumnName("excluded_topics").HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();

        builder.Property<List<string>>("_preferredTopics")
            .HasColumnName("preferred_topics").HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();

        builder.Property<List<DayOfWeek>>("_publishDays")
            .HasColumnName("publish_days").HasColumnType("integer[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();

        builder.Ignore(p => p.ExcludedTopics);
        builder.Ignore(p => p.PreferredTopics);
        builder.Ignore(p => p.PublishDays);
        builder.Ignore(p => p.TotalItemsPerWeek);

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany()
            .HasForeignKey(p => p.BrandId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.BrandId).IsUnique();

        // The scheduler scans for brands due to generate; the filter keeps it to the ones
        // that actually opted in.
        builder.HasIndex(p => p.GenerationDay)
            .HasDatabaseName("ix_content_preferences_scheduled")
            .HasFilter("scheduled_generation_enabled");
    }
}

/// <summary>
/// Reference data, shared across tenants and deliberately not tenant-owned: it seeds a new
/// brand and is then forgotten.
/// </summary>
public sealed class IndustryProfileConfiguration : IEntityTypeConfiguration<IndustryProfile>
{
    public void Configure(EntityTypeBuilder<IndustryProfile> builder)
    {
        builder.ToTable("industry_profiles");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Key).HasMaxLength(60).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(160).IsRequired();
        builder.Property(i => i.Detail).HasJsonb(new IndustryDetail());

        builder.HasIndex(i => i.Key).IsUnique();
    }
}

internal static class JsonbPropertyExtensions
{
    /// <summary>
    /// Stores a record as jsonb with a fallback for a null or unreadable column, so a shape
    /// change cannot make an existing row unloadable.
    /// </summary>
    public static PropertyBuilder<T> HasJsonb<T>(this PropertyBuilder<T> builder, T fallback)
        where T : class =>
        builder
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, PersistenceJson.Options),
                v => JsonSerializer.Deserialize<T>(v, PersistenceJson.Options) ?? fallback)
            .IsRequired();
}
