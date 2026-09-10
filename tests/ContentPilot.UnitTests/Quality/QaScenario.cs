using ContentPilot.Application.Quality;
using ContentPilot.Rendering.Contracts;
using ContractAssetKind = ContentPilot.Rendering.Contracts.AssetKind;

namespace ContentPilot.UnitTests.Quality;

/// <summary>
/// One known-good render, described exactly as the renderer would report it, plus the
/// smallest possible mutation surface. Every test below starts from the clean scenario and
/// changes one measurement — which is the same shape as the fidelity corpus in the renderer
/// tests, and for the same reason: a check is only trustworthy if the clean case is proven
/// to stay clean while a single defect is introduced.
/// </summary>
internal static class QaScenario
{
    public const string Headline = "headline";
    public const string Body = "body";
    public const string Screenshot = "screenshot";
    public const string Logo = "logo";
    public const string Background = "background";

    /// <summary>Source width/height of the logo asset. The rendered box must match it.</summary>
    public const double LogoSourceAspect = 4.0;

    public static TemplateManifest Manifest() => new()
    {
        TemplateId = "phone-floating",
        Version = 3,
        Name = "Phone floating",
        ContentTypes = [TemplateContentType.Static],
        AspectRatios = [AspectRatio.FourFive, AspectRatio.OneOne],
        TextSlots =
        [
            new TextSlot { Id = Headline, Role = "headline", MaxChars = 60, MaxLines = 3, ShrinkToFit = true },
            new TextSlot { Id = Body, Role = "body", MaxChars = 140, MaxLines = 4 },
        ],
        AssetSlots =
        [
            new AssetSlot
            {
                Id = Screenshot,
                Kind = ContractAssetKind.ProductScreenshot,
                Required = true,
                Immutable = true,
                MaxOcclusion = 0.02,
            },
            new AssetSlot
            {
                Id = Logo,
                Kind = ContractAssetKind.Logo,
                Required = false,
                MinWidthPx = 120,
                ClearSpaceRatio = 0.25,
            },
        ],
    };

    /// <summary>
    /// The frame is 1080x1350 device pixels. Boxes are laid out so nothing touches anything
    /// else — including the logo's clear space, which is a neighbour test.
    /// </summary>
    public static SlotMeasurement Slot(
        string id,
        double x,
        double y,
        double width,
        double height,
        double fontSizePx = 0,
        double? contrast = null,
        int lines = 0) => new()
    {
        SlotId = id,
        Box = new BoundingBox { X = x, Y = y, Width = width, Height = height },
        Overflows = false,
        LineCount = lines,
        FontSizePx = fontSizePx,
        ContrastRatio = contrast,
        BackdropLuminance = contrast is null ? null : 0.08,
        Occlusion = 0,
        BreaksSafeArea = false,
    };

    public static RenderReport CleanReport() => new()
    {
        Width = 1080,
        Height = 1350,
        DeviceScaleFactor = 2,
        RenderDurationMs = 940,
        FontsLoaded = ["Archivo", "Inter"],
        Slots =
        [
            // 64 device px is 32 CSS px: large text by the WCAG definition.
            Slot(Headline, 60, 80, 960, 200, fontSizePx: 64, contrast: 9.4, lines: 2),
            Slot(Body, 60, 300, 960, 120, fontSizePx: 32, contrast: 7.1, lines: 2),
            Slot(Screenshot, 180, 460, 720, 640),
            Slot(Logo, 60, 1200, 160, 40),
        ],
    };

    public static DeterministicQaInput Clean() => new()
    {
        Manifest = Manifest(),
        AspectRatio = AspectRatio.FourFive,
        Report = CleanReport(),
        ExpectedFonts = ["Archivo", "Inter"],
        SourceAspectRatios = new Dictionary<string, double> { [Logo] = LogoSourceAspect },
        Fidelity = [CleanFidelity()],
        File = new RenderedFile { Width = 1080, Height = 1350, Bytes = 412_880 },
    };

    public static CompareResult CleanFidelity() => Fidelity(FidelityVerdict.Pass);

    public static CompareResult Fidelity(
        FidelityVerdict verdict,
        double occlusion = 0.001,
        double aspectDelta = 0.0009,
        int phash = 6,
        double ssim = 0.9872,
        double deltaE = 0.24,
        params string[] reasons) => new()
    {
        SlotId = Screenshot,
        Verdict = verdict,
        Reasons = reasons,
        Metrics = new FidelityMetrics
        {
            Occlusion = occlusion,
            AspectDelta = aspectDelta,
            PerceptualHashDistance = phash,
            StructuralSimilarity = ssim,
            MeanDeltaE = deltaE,
        },
    };

    /// <summary>Replaces one slot measurement, leaving every other measurement untouched.</summary>
    public static DeterministicQaInput With(
        this DeterministicQaInput input,
        string slotId,
        Func<SlotMeasurement, SlotMeasurement> mutate) =>
        input with
        {
            Report = input.Report with
            {
                Slots = [.. input.Report.Slots.Select(s =>
                    string.Equals(s.SlotId, slotId, StringComparison.Ordinal) ? mutate(s) : s)],
            },
        };

    public static DeterministicQaInput WithSlots(
        this DeterministicQaInput input,
        params SlotMeasurement[] slots) =>
        input with { Report = input.Report with { Slots = slots } };
}
