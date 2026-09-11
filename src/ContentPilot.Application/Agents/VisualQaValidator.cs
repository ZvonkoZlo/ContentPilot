using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Checks gate 2's output against the one thing that actually matters here: every code the
/// model named is a real <c>QaFindingCode</c> that belongs to the Visual gate. A model that
/// invents a code, or reaches for one <see cref="DeterministicQaSuite"/> already owns, fails
/// validation and is repaired — the closed enum stays closed even though the model producing
/// it is not under this codebase's control.
/// <para>
/// Also converts a validated output into the domain <see cref="QaFinding"/> list the rest of
/// the pipeline consumes — called only after <see cref="Validate"/> returns no problems, so
/// every code is guaranteed to parse and belong to this gate by the time it runs.
/// </para>
/// </summary>
public static class VisualQaValidator
{
    public static IReadOnlyList<string> Validate(VisualQaOutput output)
    {
        var problems = new List<string>();

        foreach (var finding in output.Findings)
        {
            if (!Enum.TryParse<QaFindingCode>(finding.Code, out var code))
            {
                problems.Add($"'{finding.Code}' is not a finding code this system recognises.");
                continue;
            }

            if (!QaFindingCodes.BelongsTo(code, QaGate.Visual))
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

    public static IReadOnlyList<QaFinding> ToFindings(VisualQaOutput output) =>
        [.. output.Findings.Select(f => new QaFinding
        {
            Code = Enum.Parse<QaFindingCode>(f.Code),
            Severity = Enum.Parse<QaSeverity>(f.Severity),
            Confidence = f.Confidence,
            Detail = f.Detail,
        })];
}
