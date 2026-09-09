namespace ContentPilot.Application.Ai;

/// <summary>
/// What a named profile resolves to. Agents ask for <c>strategist</c>; which model that is,
/// how hard it thinks, and what it costs are configuration.
/// <para>
/// The prices are here rather than looked up at billing time on purpose: a rate change must
/// not silently rewrite what last month's campaign cost. Every <c>CostEntry</c> snapshots
/// the rate that was in force when the call was made.
/// </para>
/// </summary>
public sealed record ModelProfile
{
    public required string Name { get; init; }

    /// <summary>
    /// Which vendor runs this profile. Declared per profile rather than globally, so one
    /// campaign can put the strategist on one vendor and the copywriter on another and
    /// compare them on the same brief.
    /// </summary>
    public required ModelProvider Provider { get; init; }

    /// <summary>An exact model id. Never a date-suffixed variant.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// How much thinking the task deserves. Strategy and creative direction repay effort;
    /// mechanical rewrites do not.
    /// </summary>
    public ModelEffort Effort { get; init; } = ModelEffort.High;

    public int MaxOutputTokens { get; init; } = 8000;

    /// <summary>Micro-cents per million input tokens. $5.00/MTok is 500_000_000.</summary>
    public required long InputPricePerMillion { get; init; }

    public required long OutputPricePerMillion { get; init; }

    /// <summary>
    /// Cached reads bill at a fraction of the input rate — the reason the brand block and
    /// the instructions sit in the cacheable system half of every prompt.
    /// </summary>
    public long CacheReadPricePerMillion { get; init; }

    public long CacheWritePricePerMillion { get; init; }

    /// <summary>
    /// Costed from the rates in force for this profile. Integer arithmetic throughout:
    /// summing floating-point fractions of a cent across thousands of calls drifts, and a
    /// budget that drifts is a budget that fails open.
    /// </summary>
    public long PriceOf(TokenUsage usage) =>
        MicroCents(usage.InputTokens, InputPricePerMillion)
        + MicroCents(usage.OutputTokens, OutputPricePerMillion)
        + MicroCents(usage.CacheReadTokens, CacheReadPricePerMillion)
        + MicroCents(usage.CacheWriteTokens, CacheWritePricePerMillion);

    private static long MicroCents(long tokens, long pricePerMillion) =>
        tokens <= 0 ? 0 : (long)((decimal)tokens * pricePerMillion / 1_000_000m);
}

/// <summary>
/// The vendors with an adapter. Each one talks to its own official SDK — nothing is routed
/// through another vendor's compatibility shim, which would quietly forfeit the features
/// these agents depend on and misreport what actually ran.
/// </summary>
public enum ModelProvider
{
    Anthropic = 0,
    OpenAi = 1,
}

/// <summary>
/// Mirrors the providers' effort levels without binding callers to any vendor enum. Not
/// every vendor supports every level; each adapter documents how it maps them.
/// </summary>
public enum ModelEffort
{
    Low = 0,
    Medium = 1,
    High = 2,
    XHigh = 3,
    Max = 4,
}

/// <summary>Resolves a profile name. Fails loudly at startup rather than mid-campaign.</summary>
public interface IModelProfileRegistry
{
    ModelProfile Get(string name);

    IReadOnlyCollection<ModelProfile> All { get; }
}
