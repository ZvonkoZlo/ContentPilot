using ContentPilot.Application.Quality.Checks;
using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Quality;

/// <summary>
/// Gate 1. Every defect it can prove, proved from measurements the renderer already took,
/// before a single token is spent.
/// <para>
/// Two properties are worth stating because everything downstream leans on them. It is
/// <b>pure</b> — same input, same findings, no clock, no I/O — which is what lets the
/// calibration corpus be a unit test rather than an integration one. And it is
/// <b>exhaustive rather than early-exit</b>: an overflowing headline does not stop the
/// contrast check, because the remediation router wants the whole picture of what is wrong
/// with an attempt, not the first thing noticed.
/// </para>
/// </summary>
public static class DeterministicQaSuite
{
    public static QaReport Run(DeterministicQaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var findings = new List<QaFinding>();

        findings.AddRange(LayoutChecks.Run(input));
        findings.AddRange(ContrastCheck.Run(input));
        findings.AddRange(LogoChecks.Run(input));
        findings.AddRange(FidelityChecks.Run(input));
        findings.AddRange(FileSanityChecks.Run(input));

        return new QaReport
        {
            Gate = QaGate.Deterministic,
            // Worst first, so a reviewer reading the top of the list reads the reason the
            // attempt failed rather than the first check that happened to run.
            Findings = [.. findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Code)],
        };
    }
}
