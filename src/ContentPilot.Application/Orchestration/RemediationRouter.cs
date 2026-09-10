using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Orchestration;

/// <summary>
/// A static, testable mapping from finding code to the earliest step that could plausibly
/// fix it, plus the escalation ladder from §8. This is the whole self-correction mechanism:
/// blind full-pipeline retries are banned because they are slow, expensive and
/// non-convergent, so every attempt after the first has to change something specific.
/// <para>
/// The router decides <em>where</em> to restart and <em>which rung</em> of the ladder an
/// attempt is on. It does not decide <em>what</em> to change about the prompt or spec at
/// that step — that is the concern of whatever runs at the restarted step, reading
/// <see cref="RemediationDecision.Rung"/> and the finding off the fresh <c>ContentRevision</c>.
/// </para>
/// </summary>
public static class RemediationRouter
{
    /// <summary>
    /// One finding's earliest plausible fix. Keyed by code rather than by gate, because two
    /// findings from the same gate can need entirely different remediation — a wrong asset
    /// id is a spec bug, a scrim is a renderer bug, and conflating them would mean retrying
    /// the wrong step half the time.
    /// </summary>
    private static readonly IReadOnlyDictionary<QaFindingCode, ContentItemStatus> BaseTargets =
        new Dictionary<QaFindingCode, ContentItemStatus>
        {
            // Layout and typography: the copy itself is what needs to change.
            [QaFindingCode.TextOverflow] = ContentItemStatus.Writing,
            [QaFindingCode.TooManyLines] = ContentItemStatus.Writing,
            [QaFindingCode.ShrinkToFitAbused] = ContentItemStatus.Writing,

            // Placement and covering: the spec picked a bad layout or scheme, not bad copy.
            [QaFindingCode.SafeAreaViolation] = ContentItemStatus.SpecAssembly,
            [QaFindingCode.LowContrast] = ContentItemStatus.SpecAssembly,
            [QaFindingCode.SlotOccluded] = ContentItemStatus.SpecAssembly,
            [QaFindingCode.RequiredSlotMissing] = ContentItemStatus.Directing,

            // Logo geometry: a rendering-preset fix, not a content one.
            [QaFindingCode.LogoDistorted] = ContentItemStatus.Rendering,
            [QaFindingCode.LogoTooSmall] = ContentItemStatus.Rendering,
            [QaFindingCode.LogoClearSpaceViolation] = ContentItemStatus.Rendering,

            // Screenshot fidelity: either the wrong asset was cited (a spec bug) or the
            // renderer altered pixels it must not touch (a template bug worth excluding).
            [QaFindingCode.ScreenshotWrongAsset] = ContentItemStatus.SpecAssembly,
            [QaFindingCode.ScreenshotOccluded] = ContentItemStatus.Directing,
            [QaFindingCode.ScreenshotDistorted] = ContentItemStatus.Directing,
            [QaFindingCode.ScreenshotAltered] = ContentItemStatus.Directing,
            [QaFindingCode.ScreenshotTinted] = ContentItemStatus.Directing,

            // File and render sanity: re-render is the whole fix.
            [QaFindingCode.WrongDimensions] = ContentItemStatus.Rendering,
            [QaFindingCode.ImplausibleFileSize] = ContentItemStatus.Rendering,
            [QaFindingCode.FontFallback] = ContentItemStatus.Rendering,
            [QaFindingCode.BlockedNetworkRequest] = ContentItemStatus.Directing,

            // Video containers: reel-specific, re-render at the composer step. Reserved
            // for Phase 7; the target is meaningful once a reel item type exists.
            [QaFindingCode.VideoDurationOutOfRange] = ContentItemStatus.Rendering,
            [QaFindingCode.VideoWrongResolution] = ContentItemStatus.Rendering,
            [QaFindingCode.VideoWrongFrameRate] = ContentItemStatus.Rendering,
            [QaFindingCode.VideoBlackFrame] = ContentItemStatus.Rendering,
            [QaFindingCode.VideoAudioMissing] = ContentItemStatus.Rendering,
            [QaFindingCode.VideoAudioPeakOutOfRange] = ContentItemStatus.Rendering,

            // Visual judgement: generated imagery is the culprit, or the template choice is.
            [QaFindingCode.BackgroundArtefact] = ContentItemStatus.AssetGeneration,
            [QaFindingCode.GarbledText] = ContentItemStatus.AssetGeneration,
            [QaFindingCode.ThumbnailIllegible] = ContentItemStatus.Directing,
            [QaFindingCode.OffBrand] = ContentItemStatus.Directing,
            [QaFindingCode.PoorComposition] = ContentItemStatus.Directing,
            [QaFindingCode.SubjectCropped] = ContentItemStatus.SpecAssembly,
            [QaFindingCode.CarouselDiscontinuity] = ContentItemStatus.SpecAssembly,

            // Marketing judgement: the copy is what has to change.
            [QaFindingCode.UngroundedClaim] = ContentItemStatus.Writing,
            [QaFindingCode.MissingCallToAction] = ContentItemStatus.Writing,
            [QaFindingCode.WeakHook] = ContentItemStatus.Writing,
            [QaFindingCode.AudienceMismatch] = ContentItemStatus.Writing,
            [QaFindingCode.ForbiddenTerm] = ContentItemStatus.Writing,
            [QaFindingCode.ToneMismatch] = ContentItemStatus.Writing,

            // Repetition escalates past the item entirely — see RequiresReplan below.
            [QaFindingCode.RepetitiveContent] = ContentItemStatus.Directing,
        };

    /// <summary>
    /// Codes needing more than a retry at some earlier step: the strategist itself has to
    /// re-plan this one item with the offending topic banned. Restarting at Directing with
    /// the same topic would just reproduce the same repetition.
    /// </summary>
    private static readonly IReadOnlySet<QaFindingCode> RequiresReplan =
        new HashSet<QaFindingCode> { QaFindingCode.RepetitiveContent };

    static RemediationRouter()
    {
        // Every code must route somewhere. A code added to the enum without an entry here
        // would silently fall through to NeedsHumanReview for every attempt, at every
        // severity — this fails at process start instead, where it is impossible to miss.
        var missing = Enum.GetValues<QaFindingCode>().Where(c => !BaseTargets.ContainsKey(c)).ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"RemediationRouter has no target for: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Among the findings a gate reported, the one worth acting on. Fixing the single worst
    /// defect routinely fixes lesser ones for free — a template swap that resolves an
    /// occluded screenshot also resolves whatever safe-area nudge came with it — so spending
    /// an attempt on the second-worst finding while the worst stands would waste it.
    /// </summary>
    public static QaFinding? PrimaryFinding(IReadOnlyList<QaFinding> findings) =>
        findings
            .Where(f => f.Severity is QaSeverity.Blocking or QaSeverity.Major)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Code)
            .FirstOrDefault();

    /// <summary>
    /// Routes one attempt. <paramref name="sourceStep"/> is the step the finding was raised
    /// at (normally <c>Validating</c>); <paramref name="qualityAttemptsSoFar"/> is the run's
    /// counter before this attempt is spent; <paramref name="previous"/> is the
    /// (code, target) pair the last attempt acted on, or null on the first one.
    /// </summary>
    public static RemediationDecision Decide(
        QaFinding finding,
        ContentItemStatus sourceStep,
        int qualityAttemptsSoFar,
        int maxQualityAttempts,
        (QaFindingCode Code, ContentItemStatus Target)? previous = null)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var rung = qualityAttemptsSoFar + 1;

        // The same fix tried twice in a row without changing the outcome means the fix
        // is not converging — escalate one rung immediately rather than spend a third
        // attempt on a variation of something already shown not to work.
        var target = BaseTargets[finding.Code];

        if (previous is { } last && last.Code == finding.Code && last.Target == target)
        {
            rung++;
        }

        if (RequiresReplan.Contains(finding.Code))
        {
            return RemediationDecision.Replan(finding.Code,
                $"{finding.Code} at {sourceStep}: this item needs the strategist to re-plan it with the topic banned.");
        }

        if (rung > maxQualityAttempts)
        {
            return RemediationDecision.ToHumanReview(
                $"{finding.Code} at {sourceStep}: quality attempts exhausted ({qualityAttemptsSoFar}/{maxQualityAttempts}).");
        }

        // The escalation ladder's last rung, regardless of which code triggered it: the
        // manifest's SafeMode template, with generous budgets and no generated imagery,
        // renders cleanly for almost any input. It is a rung, not a code-specific fix.
        if (rung == maxQualityAttempts)
        {
            return RemediationDecision.SafeModeFallback(finding.Code,
                $"{finding.Code} at {sourceStep}: attempt {rung}/{maxQualityAttempts}, falling back to the SafeMode template.");
        }

        if (!ItemStateMachine.IsValidRemediationTarget(sourceStep, target))
        {
            // A target later than the step that raised the finding would be a cycle with
            // no state change — the loop-safety rule in §8. Falling back one full rung to
            // SafeMode is always a valid answer, because Directing sits at or before every
            // step that can raise a finding.
            return RemediationDecision.SafeModeFallback(finding.Code,
                $"{finding.Code} at {sourceStep}: no valid restart target at or before the source step; falling back to SafeMode.");
        }

        return RemediationDecision.Restart(finding.Code, target, rung,
            $"{finding.Code} at {sourceStep}: attempt {rung}/{maxQualityAttempts}, restarting at {target}.");
    }
}

public enum RemediationOutcome
{
    /// <summary>Restart the item at a specific earlier step.</summary>
    Restart,

    /// <summary>The ladder's last rung: restart at Directing, forcing the SafeMode template.</summary>
    SafeModeFallback,

    /// <summary>The strategist must re-plan this one item; the current attempt cannot fix it.</summary>
    Replan,

    /// <summary>Attempts are exhausted. The best attempt so far is promoted; a human decides next.</summary>
    NeedsHumanReview,
}

public sealed record RemediationDecision
{
    public required RemediationOutcome Outcome { get; init; }

    /// <summary>Null only for NeedsHumanReview, where the decision is no longer about one finding.</summary>
    public QaFindingCode? Code { get; init; }

    /// <summary>Set only when <see cref="Outcome"/> is Restart or SafeModeFallback.</summary>
    public ContentItemStatus? RestartAt { get; init; }

    /// <summary>Which rung of the escalation ladder this attempt is on, 1-based.</summary>
    public int Rung { get; init; }

    public required string Reason { get; init; }

    public static RemediationDecision Restart(QaFindingCode code, ContentItemStatus target, int rung, string reason) => new()
    {
        Outcome = RemediationOutcome.Restart,
        Code = code,
        RestartAt = target,
        Rung = rung,
        Reason = reason,
    };

    public static RemediationDecision SafeModeFallback(QaFindingCode code, string reason) => new()
    {
        Outcome = RemediationOutcome.SafeModeFallback,
        Code = code,
        RestartAt = ContentItemStatus.Directing,
        Rung = WorkflowRungForSafeMode,
        Reason = reason,
    };

    public static RemediationDecision Replan(QaFindingCode code, string reason) => new()
    {
        Outcome = RemediationOutcome.Replan,
        Code = code,
        Reason = reason,
    };

    public static RemediationDecision ToHumanReview(string reason) => new()
    {
        Outcome = RemediationOutcome.NeedsHumanReview,
        Reason = reason,
    };

    // The rung number reported on a SafeMode fallback is nominal — SafeMode is always the
    // ladder's final rung by definition, however it was reached (normal progression or an
    // immediate escalation from a repeated fix).
    private const int WorkflowRungForSafeMode = int.MaxValue;
}
