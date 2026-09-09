using System.ComponentModel.DataAnnotations;
using ContentPilot.Application.Ai;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Configuration for the language model layer. Validated at startup, because a profile that
/// resolves to nothing is a failure worth having before a campaign spends anything.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// Never in configuration files. Supplied by environment variable in development and by
    /// the platform's secret store in production.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// When false, any attempt to reach the provider throws. This is the default, so a
    /// misconfigured test run fails loudly instead of quietly spending money.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Replay recorded responses instead of calling the provider. What makes the agent
    /// tests runnable in CI without a key.
    /// </summary>
    public CassetteMode Cassettes { get; set; } = CassetteMode.Off;

    public string CassetteDirectory { get; set; } = "cassettes";

    /// <summary>Ceiling for one campaign, in micro-cents. $2.00 by default.</summary>
    public long CampaignBudgetMicroCents { get; set; } = 200_000_000;

    /// <summary>Transient failures only. Schema rejections are counted separately.</summary>
    public int MaxTransientRetries { get; set; } = 3;

    public int RequestTimeoutSeconds { get; set; } = 300;

    [Required]
    public Dictionary<string, ModelProfileOptions> Profiles { get; set; } = [];
}

public sealed class ModelProfileOptions
{
    [Required]
    public string ModelId { get; set; } = string.Empty;

    public ModelEffort Effort { get; set; } = ModelEffort.High;

    public int MaxOutputTokens { get; set; } = 8000;

    public long InputPricePerMillion { get; set; }

    public long OutputPricePerMillion { get; set; }

    public long CacheReadPricePerMillion { get; set; }

    public long CacheWritePricePerMillion { get; set; }
}

public enum CassetteMode
{
    /// <summary>Always call the provider.</summary>
    Off = 0,

    /// <summary>Call the provider and write every response to disk.</summary>
    Record = 1,

    /// <summary>Serve from disk. A missing cassette is an error, never a silent live call.</summary>
    Replay = 2,
}
