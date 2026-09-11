using System.Text.Json.Serialization;

namespace ContentPilot.Application.Agents;

/// <summary>
/// What the strategist returns: a week's worth of decisions about what is worth saying.
/// <para>
/// Note what is absent. No captions, no template choices, no image descriptions. The
/// strategist decides subjects; later agents are better at the rest, and letting one agent
/// do everything is how you get a week that is individually plausible and collectively
/// incoherent.
/// </para>
/// </summary>
public sealed record WeeklyPlan
{
    /// <summary>The single idea a reader should come away with. Keeps the week a sequence.</summary>
    [JsonPropertyName("theme")]
    public required string Theme { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<PlannedItem> Items { get; init; }
}

public sealed record PlannedItem
{
    /// <summary>"StaticPost", "Carousel" or "Reel". Counted against the quota exactly.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    /// <summary>problem-solution, social-proof, feature-highlight, and so on.</summary>
    [JsonPropertyName("pillar")]
    public required string Pillar { get; init; }

    /// <summary>What this item is for. Read by the copywriter as its brief.</summary>
    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("publish_day")]
    public required string PublishDay { get; init; }

    /// <summary>
    /// The fact keys this item relies on. Empty is legitimate — a problem-led hook makes no
    /// factual claim — but a key that does not resolve is a fabrication, and the validator
    /// rejects the plan rather than letting the copywriter build on it.
    /// </summary>
    [JsonPropertyName("fact_keys")]
    public required IReadOnlyList<string> FactKeys { get; init; }
}

/// <summary>
/// The schema sent to the provider. Written by hand rather than generated, because both
/// vendors enforce it strictly: every object must list all of its properties in
/// <c>required</c> and set <c>additionalProperties: false</c>. A generator that emits
/// optional properties produces a schema OpenAI rejects at request time.
/// </summary>
public static class WeeklyPlanSchema
{
    public const string Json = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["theme", "items"],
      "properties": {
        "theme": {
          "type": "string",
          "description": "One short sentence: the single idea a reader should come away with."
        },
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["type", "topic", "pillar", "objective", "publish_day", "fact_keys"],
            "properties": {
              "type": { "type": "string", "enum": ["StaticPost", "Carousel", "Reel"] },
              "topic": {
                "type": "string",
                "description": "What this item is about, led by the reader's problem rather than a feature."
              },
              "pillar": {
                "type": "string",
                "description": "The content pillar, e.g. problem-solution, social-proof, feature-highlight."
              },
              "objective": {
                "type": "string",
                "description": "What this item is for. The copywriter reads this as its brief."
              },
              "publish_day": {
                "type": "string",
                "enum": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
              },
              "fact_keys": {
                "type": "array",
                "description": "Fact keys this item relies on. Empty when it makes no factual claim.",
                "items": { "type": "string" }
              }
            }
          }
        }
      }
    }
    """;
}
