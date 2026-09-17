using ContentPilot.Rendering.Contracts;
using ImageMagick;

namespace ContentPilot.Renderer.Imaging;

/// <summary>
/// Verifies that a product screenshot survived the render unchanged apart from uniform
/// scaling.
/// <para>
/// The strong guarantee is structural and comes earlier: no model ever touches these
/// pixels, and the spec validator refuses a generated asset in an immutable slot. What
/// runs here is a regression check against renderer bugs — a stray overlay, a wrong crop,
/// an unintended opacity, a gradient scrim that swallows the UI.
/// </para>
/// <para>
/// The subtlety that makes naive pixel comparison useless: the screenshot is legitimately
/// scaled down, and downscaling genuinely destroys high-frequency detail. So both sides are
/// brought to one canonical size with one filter before anything is measured. Normalising
/// only the reference leaves the result dominated by the difference between our resampling
/// and Chromium's — worth about 0.1 of structural similarity on dense UI, on a render that
/// is in fact perfect.
/// </para>
/// </summary>
public sealed class FidelityComparer(ILogger<FidelityComparer> logger)
{
    public CompareResult Compare(CompareRequest request)
    {
        var thresholds = request.Thresholds;
        var reasons = new List<string>();

        using var rendered = new MagickImage(request.Rendered.ToBytes());
        using var reference = new MagickImage(request.Reference.ToBytes());

        MagickImage? mask = null;
        BoundingBox? box = request.SlotBox;
        double occlusion = 0;

        try
        {
            if (request.Mask is not null)
            {
                mask = new MagickImage(request.Mask.ToBytes());

                // What the mask measures beats what the layout reported: if a template
                // mis-sizes a slot, this is the box that actually got painted.
                box = MaskAnalyzer.MeasureWhiteBounds(mask) ?? box;

                if (box is not null)
                {
                    occlusion = MaskAnalyzer.MeasureOcclusion(mask, box);
                }
            }

            if (box is null)
            {
                throw new ArgumentException(
                    "A comparison needs either a mask render or an explicit slot box.", nameof(request));
            }

            if (occlusion > thresholds.MaxOcclusion)
            {
                reasons.Add($"{occlusion:P1} of the slot is covered by other elements (limit {thresholds.MaxOcclusion:P1}).");
            }

            // The slot box is rarely the asset's aspect, and the renderer forces
            // object-fit: contain, so the image sits letterboxed inside it. Comparing the
            // whole box would measure the bars, not the product UI.
            var contentBox = ContainedContentBox(box, reference.Width, reference.Height);

            using var crop = Crop(rendered, contentBox);

            var aspectDelta = AspectDelta(reference, crop);

            if (aspectDelta > thresholds.MaxAspectDelta)
            {
                reasons.Add($"Aspect ratio changed by {aspectDelta:P2}; the source may be scaled but never distorted.");
            }

            // Both sides are brought to one canonical size with one filter. Resizing only
            // the reference would leave the measurement dominated by the difference between
            // our resampling and Chromium's, which on dense UI costs roughly 0.1 of
            // structural similarity on a render that is in fact perfect.
            using var normalisedReference = Normalise(reference, crop.Width, crop.Height);
            using var normalisedCrop = Normalise(crop, crop.Width, crop.Height);

            var hashDistance = PerceptualHash.Distance(normalisedReference, normalisedCrop);
            var ssim = StructuralSimilarity(normalisedReference, normalisedCrop);
            var deltaE = ColorDifference.MeanDeltaE2000(normalisedReference, normalisedCrop);

            // A DCT hash is a useful wrong-picture signal, but sparse UI screenshots have
            // many low-frequency coefficients close to the median. Browser resampling can
            // flip those bits even when the pixels are structurally unchanged. Confirm the
            // hash with the finer structural metric before calling this a different asset.
            if (hashDistance > thresholds.MaxPerceptualHashDistance &&
                ssim < thresholds.FailStructuralSimilarity)
            {
                reasons.Add($"Perceptual hash distance {hashDistance}: the slot holds a different image entirely.");

                return Fail(request.SlotId, reasons, occlusion, aspectDelta, hashDistance, ssim, deltaE, box);
            }

            var verdict = Decide(ssim, deltaE, thresholds, reasons);

            if (reasons.Count > 0)
            {
                logger.LogInformation(
                    "Fidelity for slot {SlotId}: {Verdict} (ssim {Ssim:F3}, dE {DeltaE:F2}, occlusion {Occlusion:P1}).",
                    request.SlotId, verdict, ssim, deltaE, occlusion);
            }

            return new CompareResult
            {
                SlotId = request.SlotId,
                Verdict = verdict,
                Reasons = reasons,
                Metrics = new FidelityMetrics
                {
                    Occlusion = occlusion,
                    AspectDelta = aspectDelta,
                    PerceptualHashDistance = hashDistance,
                    StructuralSimilarity = ssim,
                    MeanDeltaE = deltaE,
                    MeasuredBox = box,
                },
            };
        }
        finally
        {
            mask?.Dispose();
        }
    }

    /// <summary>
    /// Occlusion, aspect and hash failures are decided before this and short-circuit; here
    /// only the similarity band remains. The middle band is the one case where a vision
    /// model earns its cost, and it is asked exactly one question.
    /// </summary>
    private static FidelityVerdict Decide(
        double ssim, double deltaE, FidelityThresholds thresholds, List<string> reasons)
    {
        if (reasons.Count > 0)
        {
            return FidelityVerdict.Fail;
        }

        if (ssim < thresholds.FailStructuralSimilarity)
        {
            reasons.Add($"Structural similarity {ssim:F3} is below the failure floor {thresholds.FailStructuralSimilarity:F2}.");
            return FidelityVerdict.Fail;
        }

        if (deltaE > thresholds.FailMeanDeltaE)
        {
            reasons.Add($"Mean colour difference {deltaE:F2} exceeds {thresholds.FailMeanDeltaE:F1}: the slot has been tinted or faded.");
            return FidelityVerdict.Fail;
        }

        if (ssim >= thresholds.PassStructuralSimilarity && deltaE <= thresholds.PassMeanDeltaE)
        {
            return FidelityVerdict.Pass;
        }

        reasons.Add($"Structural similarity {ssim:F3} and colour difference {deltaE:F2} fall in the review band.");

        return FidelityVerdict.NeedsVisualReview;
    }

    /// <summary>
    /// ImageMagick reports structural dissimilarity, defined as (1 - SSIM) / 2. Converting
    /// rather than using the SSIM metric directly avoids depending on which convention a
    /// given build reports.
    /// </summary>
    private static double StructuralSimilarity(IMagickImage<ushort> a, IMagickImage<ushort> b)
    {
        var dissimilarity = a.Compare(b, ErrorMetric.StructuralDissimilarity);

        return Math.Clamp(1 - (2 * dissimilarity), 0, 1);
    }

    private static double AspectDelta(IMagickImage<ushort> reference, IMagickImage<ushort> crop)
    {
        var referenceAspect = (double)reference.Width / reference.Height;
        var cropAspect = (double)crop.Width / crop.Height;

        return Math.Abs(referenceAspect - cropAspect) / referenceAspect;
    }

    /// <summary>
    /// Where <c>object-fit: contain</c> actually places the image inside its slot: scaled
    /// to fit, centred, with bars on the two remaining sides. Cropping to this rather than
    /// to the slot box is what keeps the comparison a like-for-like one.
    /// </summary>
    internal static BoundingBox ContainedContentBox(BoundingBox slot, uint referenceWidth, uint referenceHeight)
    {
        if (referenceWidth == 0 || referenceHeight == 0 || slot.Width <= 0 || slot.Height <= 0)
        {
            return slot;
        }

        var scale = Math.Min(slot.Width / referenceWidth, slot.Height / referenceHeight);

        var width = referenceWidth * scale;
        var height = referenceHeight * scale;

        return new BoundingBox
        {
            X = slot.X + ((slot.Width - width) / 2),
            Y = slot.Y + ((slot.Height - height) / 2),
            Width = width,
            Height = height,
        };
    }

    /// <summary>
    /// Brings an image to the comparison space: the rendered size, capped at a canonical
    /// width. The cap matters — comparing at full device resolution measures resampling
    /// differences, not tampering, while 480 px still resolves a blur, an overlay or a
    /// tint easily.
    /// </summary>
    private static MagickImage Normalise(IMagickImage<ushort> image, uint targetWidth, uint targetHeight)
    {
        const uint CanonicalWidth = 480;

        // Always resample both sides at least once. When a narrow slot was already below
        // CanonicalWidth, the reference took ImageMagick's downscale path while Chromium's
        // crop stayed byte-for-byte at its rendered size. The result then measured the two
        // resamplers rather than the screenshot, which showed up with real 738x1600 phone
        // captures in the compact square feature-highlight layout.
        var width = Math.Min(CanonicalWidth, (uint)Math.Max(1, Math.Round(targetWidth * 0.75)));
        var height = (uint)Math.Max(1, Math.Round(targetHeight * (width / (double)targetWidth)));

        var clone = (MagickImage)image.Clone();
        clone.ColorSpace = ColorSpace.sRGB;
        clone.FilterType = FilterType.Triangle;
        clone.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });

        return clone;
    }

    private static MagickImage Crop(IMagickImage<ushort> image, BoundingBox box)
    {
        var (x0, y0, x1, y1) = MaskAnalyzer.Clamp(box, image.Width, image.Height);

        var clone = (MagickImage)image.Clone();
        clone.Crop(new MagickGeometry(x0, y0, (uint)Math.Max(1, x1 - x0), (uint)Math.Max(1, y1 - y0)));
        clone.ResetPage();

        return clone;
    }

    private static CompareResult Fail(
        string slotId, IReadOnlyList<string> reasons, double occlusion, double aspectDelta,
        int hashDistance, double ssim, double deltaE, BoundingBox? box) =>
        new()
        {
            SlotId = slotId,
            Verdict = FidelityVerdict.Fail,
            Reasons = reasons,
            Metrics = new FidelityMetrics
            {
                Occlusion = occlusion,
                AspectDelta = aspectDelta,
                PerceptualHashDistance = hashDistance,
                StructuralSimilarity = ssim,
                MeanDeltaE = deltaE,
                MeasuredBox = box,
            },
        };
}

/// <summary>
/// DCT-based perceptual hash. Used as a candidate signal for "is this even the same
/// picture"; the comparer confirms it with structural similarity before failing an asset.
/// </summary>
public static class PerceptualHash
{
    private const int SampleSize = 32;
    private const int LowFrequency = 8;

    public static int Distance(IMagickImage<ushort> a, IMagickImage<ushort> b) =>
        System.Numerics.BitOperations.PopCount(Compute(a) ^ Compute(b));

    public static ulong Compute(IMagickImage<ushort> image)
    {
        using var small = (MagickImage)image.Clone();
        small.ColorSpace = ColorSpace.Gray;
        small.FilterType = FilterType.Box;
        small.Resize(new MagickGeometry(SampleSize, SampleSize) { IgnoreAspectRatio = true });

        var values = new double[SampleSize, SampleSize];

        using (var pixels = small.GetPixels())
        {
            for (var y = 0; y < SampleSize; y++)
            {
                for (var x = 0; x < SampleSize; x++)
                {
                    values[y, x] = pixels.GetPixel(x, y).GetChannel(0) / (double)ushort.MaxValue;
                }
            }
        }

        var dct = Dct2D(values);

        // Skip [0,0]: it is average brightness, which carries no structure.
        var coefficients = new List<double>(LowFrequency * LowFrequency);

        for (var y = 0; y < LowFrequency; y++)
        {
            for (var x = 0; x < LowFrequency; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                coefficients.Add(dct[y, x]);
            }
        }

        var sorted = coefficients.OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];

        ulong hash = 0;
        var bit = 0;

        for (var y = 0; y < LowFrequency; y++)
        {
            for (var x = 0; x < LowFrequency; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                if (dct[y, x] > median)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    private static double[,] Dct2D(double[,] input)
    {
        var output = new double[LowFrequency, LowFrequency];

        for (var u = 0; u < LowFrequency; u++)
        {
            for (var v = 0; v < LowFrequency; v++)
            {
                double sum = 0;

                for (var y = 0; y < SampleSize; y++)
                {
                    for (var x = 0; x < SampleSize; x++)
                    {
                        sum += input[y, x]
                            * Math.Cos((2 * x + 1) * v * Math.PI / (2 * SampleSize))
                            * Math.Cos((2 * y + 1) * u * Math.PI / (2 * SampleSize));
                    }
                }

                var cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                var cv = v == 0 ? 1 / Math.Sqrt(2) : 1;

                output[u, v] = 0.25 * cu * cv * sum;
            }
        }

        return output;
    }
}

/// <summary>
/// Mean CIEDE2000 over a coarse grid. Structural similarity on luminance is nearly blind to
/// a uniform colour cast, which is exactly what a translucent overlay, a wrong colour
/// profile or a blend mode produces — so this covers the gap it leaves.
/// </summary>
public static class ColorDifference
{
    private const int Grid = 16;

    public static double MeanDeltaE2000(IMagickImage<ushort> a, IMagickImage<ushort> b)
    {
        using var sampleA = Downsample(a);
        using var sampleB = Downsample(b);

        using var pixelsA = sampleA.GetPixels();
        using var pixelsB = sampleB.GetPixels();

        double total = 0;
        var count = 0;

        for (var y = 0; y < Grid; y++)
        {
            for (var x = 0; x < Grid; x++)
            {
                var pa = pixelsA.GetPixel(x, y);
                var pb = pixelsB.GetPixel(x, y);

                var labA = ToLab(pa.GetChannel(0), pa.GetChannel(1), pa.GetChannel(2));
                var labB = ToLab(pb.GetChannel(0), pb.GetChannel(1), pb.GetChannel(2));

                total += DeltaE2000(labA, labB);
                count++;
            }
        }

        return count == 0 ? 0 : total / count;
    }

    private static MagickImage Downsample(IMagickImage<ushort> image)
    {
        var clone = (MagickImage)image.Clone();
        clone.ColorSpace = ColorSpace.sRGB;
        clone.FilterType = FilterType.Box;
        clone.Resize(new MagickGeometry(Grid, Grid) { IgnoreAspectRatio = true });

        return clone;
    }

    private static (double L, double A, double B) ToLab(ushort r16, ushort g16, ushort b16)
    {
        double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        var r = Linear(r16 / (double)ushort.MaxValue);
        var g = Linear(g16 / (double)ushort.MaxValue);
        var b = Linear(b16 / (double)ushort.MaxValue);

        // sRGB to XYZ, D65.
        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        var y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;

        double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);

        var fx = F(x);
        var fy = F(y);
        var fz = F(z);

        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double DeltaE2000((double L, double A, double B) p, (double L, double A, double B) q)
    {
        const double kL = 1, kC = 1, kH = 1;

        var lBar = (p.L + q.L) / 2;
        var c1 = Math.Sqrt(p.A * p.A + p.B * p.B);
        var c2 = Math.Sqrt(q.A * q.A + q.B * q.B);
        var cBar = (c1 + c2) / 2;

        var g = 0.5 * (1 - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25, 7))));

        var a1 = (1 + g) * p.A;
        var a2 = (1 + g) * q.A;

        var c1p = Math.Sqrt(a1 * a1 + p.B * p.B);
        var c2p = Math.Sqrt(a2 * a2 + q.B * q.B);
        var cBarP = (c1p + c2p) / 2;

        var h1p = Angle(p.B, a1);
        var h2p = Angle(q.B, a2);

        var deltaLp = q.L - p.L;
        var deltaCp = c2p - c1p;

        double deltahp;

        if (c1p * c2p == 0)
        {
            deltahp = 0;
        }
        else if (Math.Abs(h2p - h1p) <= 180)
        {
            deltahp = h2p - h1p;
        }
        else
        {
            deltahp = h2p > h1p ? h2p - h1p - 360 : h2p - h1p + 360;
        }

        var deltaHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Radians(deltahp) / 2);

        double hBarP;

        if (c1p * c2p == 0)
        {
            hBarP = h1p + h2p;
        }
        else if (Math.Abs(h1p - h2p) <= 180)
        {
            hBarP = (h1p + h2p) / 2;
        }
        else
        {
            hBarP = h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
        }

        var t = 1
            - 0.17 * Math.Cos(Radians(hBarP - 30))
            + 0.24 * Math.Cos(Radians(2 * hBarP))
            + 0.32 * Math.Cos(Radians(3 * hBarP + 6))
            - 0.20 * Math.Cos(Radians(4 * hBarP - 63));

        var sl = 1 + (0.015 * Math.Pow(lBar - 50, 2)) / Math.Sqrt(20 + Math.Pow(lBar - 50, 2));
        var sc = 1 + 0.045 * cBarP;
        var sh = 1 + 0.015 * cBarP * t;

        var deltaTheta = 30 * Math.Exp(-Math.Pow((hBarP - 275) / 25, 2));
        var rc = 2 * Math.Sqrt(Math.Pow(cBarP, 7) / (Math.Pow(cBarP, 7) + Math.Pow(25, 7)));
        var rt = -rc * Math.Sin(2 * Radians(deltaTheta));

        var lTerm = deltaLp / (kL * sl);
        var cTerm = deltaCp / (kC * sc);
        var hTerm = deltaHp / (kH * sh);

        return Math.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + rt * cTerm * hTerm);
    }

    private static double Angle(double b, double a)
    {
        if (a == 0 && b == 0)
        {
            return 0;
        }

        var degrees = Math.Atan2(b, a) * 180 / Math.PI;

        return degrees >= 0 ? degrees : degrees + 360;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
