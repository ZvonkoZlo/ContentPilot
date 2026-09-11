using System.Text.Json.Serialization;

namespace ContentPilot.Application.Agents;

/// <summary>
/// What gate 2 returns: a list from the closed enum, each with a confidence value. Empty is
/// the expected answer for a clean image — the prompt says so explicitly, because a vision
/// model asked "is anything wrong?" will otherwise always find something, and that is how a
/// QA gate turns into infinite remediation.
/// </summary>
public sealed record VisualQaOutput
{
    [JsonPropertyName("findings")]
    public required IReadOnlyList<VisualQaFinding> Findings { get; init; }
}

public sealed record VisualQaFinding
{
    /// <summary>A <c>QaFindingCode</c> name from the 6xx band. Validated, never trusted.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>
    /// 0 to 1. A finding below the router's confidence threshold is still recorded — it is
    /// evidence, and discarding it would make the QA pass-rate metric lie — but it never
    /// triggers remediation on its own.
    /// </summary>
    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

/// <summary>
/// Hand-written like every other schema in this codebase: both providers enforce every
/// property listed in <c>required</c> with <c>additionalProperties: false</c>.
/// </summary>
public static class VisualQaOutputSchema
{
    public const string Json = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["findings"],
      "properties": {
        "findings": {
          "type": "array",
          "description": "Empty when the image is clean. Never pad this list with a merely-fine observation.",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["code", "severity", "confidence", "detail"],
            "properties": {
              "code": {
                "type": "string",
                "enum": [
                  "BackgroundArtefact", "GarbledText", "ThumbnailIllegible",
                  "OffBrand", "PoorComposition", "SubjectCropped", "CarouselDiscontinuity"
                ]
              },
              "severity": { "type": "string", "enum": ["Minor", "Major", "Blocking"] },
              "confidence": { "type": "number", "description": "0 to 1, how sure you are." },
              "detail": { "type": "string", "description": "One sentence, specific enough for a human to see what you saw." }
            }
          }
        }
      }
    }
    """;
}
