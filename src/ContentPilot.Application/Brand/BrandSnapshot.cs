using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContentPilot.Domain.Branding;

namespace ContentPilot.Application.Brand;

/// <summary>
/// Everything the agents are allowed to know about a brand, composed once at campaign start
/// and then frozen.
/// <para>
/// Agents receive this object and never the database. That is what makes a run
/// reproducible: the same snapshot plus the same prompt version plus the same model
/// produces a comparable result, and an edit to the live profile mid-week cannot change
/// what a campaign already in flight is working from.
/// </para>
/// </summary>
public sealed record BrandSnapshot
{
    public required Guid BrandId { get; init; }

    public required string Name { get; init; }

    public string? Website { get; init; }

    public required string PrimaryLanguage { get; init; }

    public required IReadOnlyList<string> Languages { get; init; }

    public required string TimeZoneId { get; init; }

    public string? Industry { get; init; }

    public required VisualIdentity Visual { get; init; }

    public required ToneOfVoice Voice { get; init; }

    public required Messaging Messaging { get; init; }

    public string? OperatorNotes { get; init; }

    public required IReadOnlyList<ProductFactView> Facts { get; init; }

    public required IReadOnlyList<PersonaView> Personas { get; init; }

    public required QuotaView Quota { get; init; }

    public required IReadOnlyList<AssetView> Assets { get; init; }

    /// <summary>SHA-256 over the canonical form. Identical brands hash identically.</summary>
    public string ComputeHash()
    {
        // Deterministic by construction: a stable property order, no indentation, and every
        // collection already sorted by the assembler. A hash that moved with formatting
        // would create a new brand version on every campaign and destroy the audit trail.
        var json = JsonSerializer.Serialize(this, SnapshotJson.Canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));

        return Convert.ToHexStringLower(bytes);
    }

    public string ToJson() => JsonSerializer.Serialize(this, SnapshotJson.Canonical);

    public static BrandSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<BrandSnapshot>(json, SnapshotJson.Canonical)
        ?? throw new InvalidOperationException("Stored brand snapshot did not deserialise.");
}

public sealed record ProductFactView
{
    /// <summary>The identifier copy must cite. Never a generated name.</summary>
    public required string Key { get; init; }

    public required string Statement { get; init; }

    public required string Category { get; init; }

    public string? Evidence { get; init; }
}

public sealed record PersonaView
{
    public required string Name { get; init; }

    public required string Segment { get; init; }

    public required bool IsPrimary { get; init; }

    public IReadOnlyList<string> Pains { get; init; } = [];

    public IReadOnlyList<string> Goals { get; init; } = [];

    public IReadOnlyList<string> Objections { get; init; } = [];

    public IReadOnlyList<string> Vocabulary { get; init; } = [];

    public string? Context { get; init; }
}

public sealed record QuotaView
{
    public required int Posts { get; init; }

    public required int Carousels { get; init; }

    public required int Reels { get; init; }

    public IReadOnlyList<string> ExcludedTopics { get; init; } = [];

    public IReadOnlyList<string> PreferredTopics { get; init; } = [];

    public IReadOnlyList<string> PublishDays { get; init; } = [];

    [JsonIgnore]
    public int Total => Posts + Carousels + Reels;
}

/// <summary>
/// What the creative director may reach for. Identifiers only — the bytes travel to the
/// renderer separately, and an agent that cannot name an asset it was not given cannot
/// invent one.
/// </summary>
public sealed record AssetView
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }

    public required string FileName { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Immutable slots accept only uploads. The director sees this and cannot guess wrong.</summary>
    public required string Origin { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Description { get; init; }
}

internal static class SnapshotJson
{
    public static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
