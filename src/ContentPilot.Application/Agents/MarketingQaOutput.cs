using System.Text.Json.Serialization;

namespace ContentPilot.Application.Agents;

/// <summary>
/// What gate 3 returns: a list from the 7xx band, each with a confidence value. Same shape
/// as <see cref="VisualQaOutput"/> and the same reason — an empty list is the expected,
/// unremarkable answer for good copy.
/// </summary>
public sealed record MarketingQaOutput
{
    [JsonPropertyName("findings")]
    public required IReadOnlyList<MarketingQaFinding> Findings { get; init; }
}

public sealed record MarketingQaFinding
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

public static class MarketingQaOutputSchema
{
    public const string Json = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["findings"],
      "properties": {
        "findings": {
          "type": "array",
          "description": "Empty when the copy is clean. Never pad this list with a merely-fine observation.",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["code", "severity", "confidence", "detail"],
            "properties": {
              "code": {
                "type": "string",
                "enum": [
                  "UngroundedClaim", "MissingCallToAction", "WeakHook",
                  "AudienceMismatch", "RepetitiveContent", "ForbiddenTerm", "ToneMismatch"
                ]
              },
              "severity": { "type": "string", "enum": ["Minor", "Major", "Blocking"] },
              "confidence": { "type": "number", "description": "0 to 1, how sure you are." },
              "detail": { "type": "string", "description": "One sentence: what claim, which slot, why it fails." }
            }
          }
        }
      }
    }
    """;
}
