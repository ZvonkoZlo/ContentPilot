using System.Text.Json;
using System.Text.Json.Serialization;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class QualityReviewConfiguration : IEntityTypeConfiguration<QualityReview>
{
    public void Configure(EntityTypeBuilder<QualityReview> builder)
    {
        builder.ToTable("quality_reviews");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Gate).HasConversion<short>().IsRequired();
        builder.Property(r => r.Outcome).HasConversion<short>().IsRequired();
        builder.Property(r => r.Attempt).IsRequired();
        builder.Property(r => r.Score).IsRequired();
        builder.Property(r => r.EvaluatedAt).IsRequired();
        builder.Ignore(r => r.HasBlockingFindings);

        builder.Property(r => r.Findings)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, QualityJson.Options),
                v => JsonSerializer.Deserialize<IReadOnlyList<QaFinding>>(v, QualityJson.Options) ?? new List<QaFinding>(),
                FindingsComparer)
            .IsRequired();

        builder.HasOne<ContentItem>()
            .WithMany().HasForeignKey(r => r.ContentItemId).OnDelete(DeleteBehavior.Cascade);

        // One row per gate per attempt. A second would mean a gate ran twice on the same
        // pixels, which is either a bug or a retry that should have bumped the attempt.
        builder.HasIndex(r => new { r.ContentItemId, r.Attempt, r.Gate }).IsUnique();

        // The QA pass-rate metric: this tenant, this gate, over time.
        builder.HasIndex(r => new { r.TenantId, r.Gate, r.EvaluatedAt });
    }

    /// <summary>
    /// The findings list is never mutated in place — the entity is append-only and exposes
    /// it read-only — so structural equality over the serialized form is both correct and
    /// the cheapest thing to give the change tracker.
    /// </summary>
    private static readonly ValueComparer<IReadOnlyList<QaFinding>> FindingsComparer = new(
        (a, b) => (a ?? new List<QaFinding>()).SequenceEqual(b ?? new List<QaFinding>()),
        v => v.Aggregate(0, (hash, finding) => HashCode.Combine(hash, finding.GetHashCode())),
        v => v.ToList());
}

/// <summary>
/// Findings are written with enum <em>names</em> rather than numbers. These rows outlive
/// every deployment that produced them and are read by humans debugging a QA regression, so
/// a column that says <c>"ScreenshotTinted"</c> is worth far more than one that says 303.
/// </summary>
internal static class QualityJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
