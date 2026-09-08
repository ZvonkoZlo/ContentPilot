using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Tenancy;

/// <summary>
/// The isolation root. One row for the MVP, but every tenant-owned table carries its
/// identifier from the first migration so multi-tenancy is never a retrofit.
/// </summary>
public sealed class Tenant : Entity, IAuditable
{
    private Tenant()
    {
        Name = null!;
        Slug = null!;
    }

    public Tenant(string name, string slug, TenantLimits? limits = null)
    {
        Name = Guard.MaxLength(Guard.NotBlank(name), 200);
        Slug = NormaliseSlug(slug);
        Limits = limits ?? TenantLimits.Default;
    }

    public string Name { get; private set; }

    /// <summary>Stable, URL- and storage-key-safe identifier.</summary>
    public string Slug { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>Cost and retry ceilings. Tuning these is data, never a deploy.</summary>
    public TenantLimits Limits { get; private set; } = TenantLimits.Default;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public void Rename(string name) => Name = Guard.MaxLength(Guard.NotBlank(name), 200);

    public void UpdateLimits(TenantLimits limits) => Limits = limits;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string NormaliseSlug(string slug)
    {
        var value = Guard.MaxLength(Guard.NotBlank(slug), 60).ToLowerInvariant();

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                throw new ArgumentException("Slug may contain only lowercase letters, digits and hyphens.", nameof(slug));
            }
        }

        return value;
    }
}

/// <summary>
/// Hard safety limits. The orchestrator reads these before every billable call; a
/// breach ends in NeedsHumanReview rather than an unbounded retry loop.
/// </summary>
public sealed record TenantLimits
{
    public static readonly TenantLimits Default = new();

    /// <summary>Quality retries per content item, excluding schema repair and transient retries.</summary>
    public int MaxQualityAttemptsPerItem { get; init; } = 3;

    /// <summary>Schema-repair retries when a model returns invalid or invalid-per-validator JSON.</summary>
    public int MaxSchemaRepairAttempts { get; init; } = 2;

    /// <summary>Provider 429/5xx and renderer timeout retries.</summary>
    public int MaxTransientAttempts { get; init; } = 3;

    /// <summary>Absolute ceiling on executed steps per item, whatever the other counters say.</summary>
    public int MaxStepsPerItem { get; init; } = 40;

    /// <summary>Wall-clock deadline for one workflow run.</summary>
    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromMinutes(45);

    public long MaxCostPerItemMicroCents { get; init; } = 60_000_000;      // $0.60

    public long MaxCostPerCampaignMicroCents { get; init; } = 600_000_000; // $6.00

    public int MaxGeneratedImageVariantsPerItem { get; init; } = 3;

    public int MaxReelRenderAttempts { get; init; } = 2;

    public int AttemptArtefactRetentionDays { get; init; } = 30;

    public int ModelPayloadRetentionDays { get; init; } = 90;
}
