using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Agents;

/// <summary>Same shape and the same reason as <see cref="VisualQaValidator"/>, for the 7xx band.</summary>
public static class MarketingQaValidator
{
    public static IReadOnlyList<string> Validate(MarketingQaOutput output)
    {
        var problems = new List<string>();

        foreach (var finding in output.Findings)
        {
            if (!Enum.TryParse<QaFindingCode>(finding.Code, out var code))
            {
                problems.Add($"'{finding.Code}' is not a finding code this system recognises.");
                continue;
            }

            if (!QaFindingCodes.BelongsTo(code, QaGate.Marketing))
            {
                problems.Add($"{finding.Code} belongs to a different gate and cannot be reported here.");
            }

            if (!Enum.TryParse<QaSeverity>(finding.Severity, out _))
            {
                problems.Add($"'{finding.Severity}' is not a severity this system recognises.");
            }

            if (finding.Confidence is < 0 or > 1)
            {
                problems.Add($"{finding.Code}: confidence must be between 0 and 1, was {finding.Confidence}.");
            }

            if (string.IsNullOrWhiteSpace(finding.Detail))
            {
                problems.Add($"{finding.Code} has no detail; a reviewer needs to see what was actually observed.");
            }
        }

        return problems;
    }

    public static IReadOnlyList<QaFinding> ToFindings(MarketingQaOutput output) =>
        [.. output.Findings.Select(f => new QaFinding
        {
            Code = Enum.Parse<QaFindingCode>(f.Code),
            Severity = Enum.Parse<QaSeverity>(f.Severity),
            Confidence = f.Confidence,
            Detail = f.Detail,
        })];
}
