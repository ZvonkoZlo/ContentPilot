namespace ContentPilot.Domain.Quality;

/// <summary>
/// One thing wrong with one attempt, said in a vocabulary the orchestrator understands.
/// <para>
/// The closed enum is the load-bearing part of the whole QA design. If a finding could
/// carry free text, deciding what to do about it would need another model call, and the
/// orchestrator would no longer be the thing that decides what happens next — remediation
/// would be negotiated between agents. A finite code set means the remediation router is a
/// switch statement, reviewable and testable, and a model that invents a new complaint
/// fails schema validation instead of inventing new control flow.
/// </para>
/// </summary>
public sealed record QaFinding
{
    public required QaFindingCode Code { get; init; }

    public required QaSeverity Severity { get; init; }

    /// <summary>The slot the finding is about, where the check is per-slot.</summary>
    public string? SlotId { get; init; }

    /// <summary>
    /// Human-readable, for the review UI and the revision journal. Nothing branches on it:
    /// every decision is made from <see cref="Code"/> and <see cref="Severity"/>.
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>What the check measured, when it measured something.</summary>
    public double? Measured { get; init; }

    /// <summary>The threshold it was measured against.</summary>
    public double? Threshold { get; init; }

    /// <summary>
    /// Model confidence, 0 to 1, for the agent gates. Null for deterministic findings —
    /// a bounding box is not 80% sure. Findings below the configured threshold are
    /// recorded but do not trigger remediation.
    /// </summary>
    public double? Confidence { get; init; }

    public static QaFinding Deterministic(
        QaFindingCode code,
        QaSeverity severity,
        string detail,
        string? slotId = null,
        double? measured = null,
        double? threshold = null) =>
        new()
        {
            Code = code,
            Severity = severity,
            Detail = detail,
            SlotId = slotId,
            Measured = measured,
            Threshold = threshold,
        };
}

/// <summary>
/// How much a finding matters. The gate outcome is derived from the worst severity present,
/// so this is what separates "fix it" from "worth knowing".
/// </summary>
public enum QaSeverity
{
    /// <summary>Recorded for the trend, never acted on.</summary>
    Info = 0,

    /// <summary>Noted on the item. Does not by itself cost an attempt.</summary>
    Minor = 1,

    /// <summary>Enough doubt to want a second opinion: escalates rather than fails outright.</summary>
    Major = 2,

    /// <summary>Certain and disqualifying. The attempt is failed without spending a token.</summary>
    Blocking = 3,
}

/// <summary>
/// Which gate produced a finding. Cheapest first: gate 1 catches roughly 70% of real
/// defects at no cost, and running it first also stops a vision model being paid to notice
/// something a bounding box already proved.
/// </summary>
public enum QaGate
{
    /// <summary>Measurement only, from the render report and the fidelity comparison.</summary>
    Deterministic = 0,

    /// <summary>Vision model, asked only what code cannot answer.</summary>
    Visual = 1,

    /// <summary>Text model: claim grounding, hook strength, repetition.</summary>
    Marketing = 2,
}

public enum QaOutcome
{
    Pass = 0,

    /// <summary>The grey zone: escalate to the next gate, or to a human at the last rung.</summary>
    NeedsReview = 1,

    Fail = 2,
}

/// <summary>
/// **Append-only.** Phase 5's remediation router and phase 6's QA agents both switch over
/// these values, and a persisted <c>QualityReview</c> stores them by name, so removing or
/// renaming one silently rewrites history. Add codes at the end of the block they belong to.
/// <para>
/// Numbering is explicit and grouped by hundreds, which keeps the wire form stable and makes
/// the gate that owns a code readable in a raw JSON payload.
/// </para>
/// </summary>
public enum QaFindingCode
{
    // 1xx — layout and typography, read straight from the render report.

    /// <summary>Content is taller or wider than its box: the text is clipped.</summary>
    TextOverflow = 100,

    /// <summary>More lines than the slot budgets, even without clipping.</summary>
    TooManyLines = 101,

    /// <summary>Shrink-to-fit had to intervene. Once is fine; a habit means the budgets are wrong.</summary>
    ShrinkToFitAbused = 102,

    /// <summary>Part of a slot sits inside the platform chrome margins.</summary>
    SafeAreaViolation = 103,

    /// <summary>WCAG ratio against the pixels actually behind the text is below the floor.</summary>
    LowContrast = 104,

    /// <summary>A slot the manifest marks required has no measurement in the report.</summary>
    RequiredSlotMissing = 105,

    /// <summary>Two slots overlap enough that one is unreadable.</summary>
    SlotOccluded = 106,

    // 2xx — brand assets.

    /// <summary>Rendered logo aspect ratio differs from the source: it has been squashed.</summary>
    LogoDistorted = 200,

    /// <summary>Below the manifest's minimum rendered width, where a logo stops being legible.</summary>
    LogoTooSmall = 201,

    /// <summary>Something is inside the logo's clear space.</summary>
    LogoClearSpaceViolation = 202,

    // 3xx — screenshot fidelity, from the mask render and the comparison.

    /// <summary>Something is painted over the product UI beyond the manifest's allowance.</summary>
    ScreenshotOccluded = 300,

    /// <summary>Non-uniform scaling. The source may be scaled, never distorted.</summary>
    ScreenshotDistorted = 301,

    /// <summary>Structure changed: a blur, a warp, a wrong crop.</summary>
    ScreenshotAltered = 302,

    /// <summary>A colour cast: an opacity overlay, a scrim, a wrong colour profile.</summary>
    ScreenshotTinted = 303,

    /// <summary>Not even the same picture. The wrong asset reached the slot.</summary>
    ScreenshotWrongAsset = 304,

    // 4xx — file and render sanity.

    /// <summary>Output dimensions do not match the requested aspect ratio.</summary>
    WrongDimensions = 400,

    /// <summary>The byte size is outside the plausible band for a real render.</summary>
    ImplausibleFileSize = 401,

    /// <summary>A font family did not resolve and silently fell back, changing the design.</summary>
    FontFallback = 402,

    /// <summary>The page tried to reach the network. It is blocked, but wanting to is a defect.</summary>
    BlockedNetworkRequest = 403,

    // 5xx — video containers. Reel QA runs the image checks on extracted keyframes and
    // these on the container itself.

    VideoDurationOutOfRange = 500,

    VideoWrongResolution = 501,

    VideoWrongFrameRate = 502,

    /// <summary>The first frame is black — the classic silent composition failure.</summary>
    VideoBlackFrame = 503,

    VideoAudioMissing = 504,

    VideoAudioPeakOutOfRange = 505,

    // 6xx — visual judgement. Gate 2 only: what a bounding box cannot answer.

    /// <summary>Generated imagery went wrong: warped geometry, extra fingers, melted objects.</summary>
    BackgroundArtefact = 600,

    /// <summary>Pseudo-text in generated imagery. Models produce it constantly.</summary>
    GarbledText = 601,

    /// <summary>Unreadable at 150 px, which is how social content is actually consumed.</summary>
    ThumbnailIllegible = 602,

    /// <summary>Colours, type or treatment are off-brand despite the tokens.</summary>
    OffBrand = 603,

    /// <summary>The composition reads as amateur: crowding, tangents, no focal point.</summary>
    PoorComposition = 604,

    /// <summary>The subject is cropped through a face, a product edge, or the logo.</summary>
    SubjectCropped = 605,

    /// <summary>Carousel slides do not read as one sequence.</summary>
    CarouselDiscontinuity = 606,

    // 7xx — marketing judgement. Gate 3 only.

    /// <summary>A factual assertion with no ProductFact citation, or one that does not support it.</summary>
    UngroundedClaim = 700,

    /// <summary>No call to action where the objective requires one.</summary>
    MissingCallToAction = 701,

    /// <summary>The hook will not stop a scroll.</summary>
    WeakHook = 702,

    /// <summary>Written past the persona rather than to it.</summary>
    AudienceMismatch = 703,

    /// <summary>Too close to something this brand already published.</summary>
    RepetitiveContent = 704,

    /// <summary>Uses a term the brand profile forbids.</summary>
    ForbiddenTerm = 705,

    /// <summary>Reads in a voice that is not the brand's.</summary>
    ToneMismatch = 706,
}

public static class QaFindingCodes
{
    /// <summary>
    /// The gate that owns each code. Used to assert that a gate cannot report a finding
    /// belonging to another one — a vision model claiming text overflow would bypass the
    /// deterministic measurement that is the actual authority on it.
    /// </summary>
    public static QaGate GateFor(QaFindingCode code) => (int)code switch
    {
        >= 600 and < 700 => QaGate.Visual,
        >= 700 => QaGate.Marketing,
        _ => QaGate.Deterministic,
    };

    public static bool BelongsTo(QaFindingCode code, QaGate gate) => GateFor(code) == gate;
}
