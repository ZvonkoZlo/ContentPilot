using System.Text.Json.Serialization;

namespace ContentPilot.Application.Agents;

/// <summary>
/// What the copywriter returns: text for every slot the chosen template declared, and the
/// fact keys it actually leaned on. Nothing about layout, colour or imagery — the copywriter
/// writes words to budgets it is handed, it does not choose the budgets.
/// </summary>
public sealed record CopySet
{
    [JsonPropertyName("slots")]
    public required IReadOnlyList<CopySlot> Slots { get; init; }

    /// <summary>
    /// Fact keys the copy actually cites, a subset of what the brief allowed. Empty is
    /// legitimate for a problem-led hook with no factual claim.
    /// </summary>
    [JsonPropertyName("fact_citations")]
    public required IReadOnlyList<string> FactCitations { get; init; }

    public string TextFor(string slotId) =>
        Slots.FirstOrDefault(s => string.Equals(s.Id, slotId, StringComparison.Ordinal))?.Text
        ?? throw new KeyNotFoundException($"No copy was written for slot '{slotId}'.");
}

public sealed record CopySlot
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Written by hand, like <see cref="WeeklyPlanSchema"/> — both vendors enforce every object
/// property in <c>required</c> with <c>additionalProperties: false</c>, which a generic
/// generator tends not to produce.
/// </summary>
public static class CopySetSchema
{
    public const string Json = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["slots", "fact_citations"],
      "properties": {
        "slots": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["id", "text"],
            "properties": {
              "id": { "type": "string", "description": "The slot id exactly as given in the brief." },
              "text": { "type": "string", "description": "The copy for this slot, within its character budget." }
            }
          }
        },
        "fact_citations": {
          "type": "array",
          "description": "Fact keys this copy actually relies on. Empty when it makes no factual claim.",
          "items": { "type": "string" }
        }
      }
    }
    """;
}
