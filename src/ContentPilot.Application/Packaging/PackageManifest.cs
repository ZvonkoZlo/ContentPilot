using System.Text.Json.Serialization;

namespace ContentPilot.Application.Packaging;

/// <summary>
/// <c>plan.json</c> — the human-readable week: theme, every item, its publish day and
/// current status. Written even for items that never finished, so a partially-ready week is
/// honest about what is missing rather than silently short.
/// </summary>
public sealed record CampaignPlan
{
    [JsonPropertyName("campaign_id")]
    public required Guid CampaignId { get; init; }

    [JsonPropertyName("week_start")]
    public required DateOnly WeekStart { get; init; }

    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("built_at")]
    public required DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<CampaignPlanItem> Items { get; init; }
}

public sealed record CampaignPlanItem
{
    [JsonPropertyName("ordinal")]
    public required int Ordinal { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    [JsonPropertyName("publish_day")]
    public required string PublishDay { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Null when the item never produced a renderable asset — nothing to package.</summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; init; }
}

/// <summary>
/// <c>manifest.json</c> — the machine-readable index every downstream tool (the ZIP builder,
/// a future publishing integration) reads instead of listing the bucket: every file this
/// build produced, its hash and size, so a client can verify a download without re-fetching
/// the whole campaign.
/// </summary>
public sealed record PackageManifest
{
    [JsonPropertyName("campaign_id")]
    public required Guid CampaignId { get; init; }

    [JsonPropertyName("built_at")]
    public required DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<PackageManifestFile> Files { get; init; }
}

public sealed record PackageManifestFile
{
    /// <summary>Relative to the campaign's own folder, e.g. <c>post-01/image.png</c>.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }

    [JsonPropertyName("content_type")]
    public required string ContentType { get; init; }
}
