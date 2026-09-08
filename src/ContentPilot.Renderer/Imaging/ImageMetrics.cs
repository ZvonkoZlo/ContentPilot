using ContentPilot.Rendering.Contracts;
using ImageMagick;

namespace ContentPilot.Renderer.Imaging;

/// <summary>
/// Reads the mask render. Inside the slot box, white is the slot and anything else is an
/// element painted over it — so occlusion is a pixel count, not an estimate.
/// </summary>
public static class MaskAnalyzer
{
    private const double WhiteThreshold = 0.75;

    public static MagickImage Load(byte[] bytes) => new(bytes);

    public static double MeasureOcclusion(byte[] maskPng, BoundingBox box)
    {
        using var mask = new MagickImage(maskPng);
        return MeasureOcclusion(mask, box);
    }

    public static double MeasureOcclusion(IMagickImage<ushort> mask, BoundingBox box)
    {
        var (x0, y0, x1, y1) = Clamp(box, mask.Width, mask.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            return 0;
        }

        using var pixels = mask.GetPixels();
        long total = 0;
        long covered = 0;

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var pixel = pixels.GetPixel(x, y);
                var luminance = Luminance(pixel.GetChannel(0), pixel.GetChannel(1), pixel.GetChannel(2));

                total++;

                if (luminance < WhiteThreshold)
                {
                    covered++;
                }
            }
        }

        return total == 0 ? 0 : (double)covered / total;
    }

    /// <summary>
    /// The slot box as the mask actually renders it. Preferred over the reported box: if a
    /// template mis-sizes a slot, this measures what really happened.
    /// </summary>
    public static BoundingBox? MeasureWhiteBounds(IMagickImage<ushort> mask)
    {
        using var pixels = mask.GetPixels();

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                var pixel = pixels.GetPixel(x, y);

                if (Luminance(pixel.GetChannel(0), pixel.GetChannel(1), pixel.GetChannel(2)) < WhiteThreshold)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        return new BoundingBox
        {
            X = minX,
            Y = minY,
            Width = maxX - minX + 1,
            Height = maxY - minY + 1,
        };
    }

    internal static (int X0, int Y0, int X1, int Y1) Clamp(BoundingBox box, uint width, uint height)
    {
        var x0 = Math.Clamp((int)Math.Round(box.X), 0, (int)width);
        var y0 = Math.Clamp((int)Math.Round(box.Y), 0, (int)height);
        var x1 = Math.Clamp((int)Math.Round(box.X + box.Width), 0, (int)width);
        var y1 = Math.Clamp((int)Math.Round(box.Y + box.Height), 0, (int)height);

        return (x0, y0, x1, y1);
    }

    internal static double Luminance(ushort r, ushort g, ushort b) =>
        (0.2126 * r + 0.7152 * g + 0.0722 * b) / ushort.MaxValue;
}

/// <summary>
/// Contrast between a text slot and what is actually behind it. The backdrop is sampled
/// from the finished render, with pixels close to the text colour excluded — the glyphs
/// themselves would otherwise drag the measurement towards the foreground and make every
/// slot look like it has perfect contrast.
/// </summary>
public sealed class ContrastAnalyzer
{
    public (double BackdropLuminance, double ContrastRatio) Measure(
        IMagickImage<ushort> render, BoundingBox box, string cssForeground)
    {
        var foreground = ParseCssColor(cssForeground);
        var foregroundLuminance = RelativeLuminance(foreground.R, foreground.G, foreground.B);

        var (x0, y0, x1, y1) = MaskAnalyzer.Clamp(box, render.Width, render.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            return (0, 1);
        }

        using var pixels = render.GetPixels();
        var samples = new List<double>(capacity: 512);

        // A coarse grid is plenty and keeps this cheap on a 1080x1350 frame.
        var stepX = Math.Max(1, (x1 - x0) / 32);
        var stepY = Math.Max(1, (y1 - y0) / 32);

        for (var y = y0; y < y1; y += stepY)
        {
            for (var x = x0; x < x1; x += stepX)
            {
                var pixel = pixels.GetPixel(x, y);

                var r = pixel.GetChannel(0) / (double)ushort.MaxValue;
                var g = pixel.GetChannel(1) / (double)ushort.MaxValue;
                var b = pixel.GetChannel(2) / (double)ushort.MaxValue;

                var luminance = RelativeLuminance(r, g, b);

                if (Math.Abs(luminance - foregroundLuminance) < 0.06)
                {
                    continue; // very likely a glyph, not the backdrop
                }

                samples.Add(luminance);
            }
        }

        if (samples.Count == 0)
        {
            // The slot is uniformly the text colour: nothing legible is behind it.
            return (foregroundLuminance, 1);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];

        var lighter = Math.Max(median, foregroundLuminance);
        var darker = Math.Min(median, foregroundLuminance);

        return (median, (lighter + 0.05) / (darker + 0.05));
    }

    /// <summary>WCAG relative luminance, which needs linearised channels.</summary>
    private static double RelativeLuminance(double r, double g, double b) =>
        0.2126 * Linearise(r) + 0.7152 * Linearise(g) + 0.0722 * Linearise(b);

    private static double Linearise(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Handles what getComputedStyle actually returns: rgb() and rgba(). Anything else
    /// falls back to mid grey rather than throwing — a colour we cannot parse should not
    /// fail a render.
    /// </summary>
    internal static (double R, double G, double B) ParseCssColor(string value)
    {
        var text = value.Trim();

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = text.IndexOf('(');
            var close = text.IndexOf(')');

            if (open > 0 && close > open)
            {
                var parts = text[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3 &&
                    double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var r) &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var g) &&
                    double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    return (r / 255.0, g / 255.0, b / 255.0);
                }
            }
        }

        // Chromium returns color-mix() results in this form, and templates lean on
        // color-mix heavily. Falling through to the grey default here silently reported
        // ~1.9:1 for perfectly legible text.
        if (text.StartsWith("color(", StringComparison.OrdinalIgnoreCase))
        {
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');

            if (open > 0 && close > open)
            {
                var parts = text[(open + 1)..close].Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries);

                // "srgb 0.97 0.76 0.85" — the space is the colour space, channels are 0..1.
                if (parts.Length >= 4 &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var cr) &&
                    double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var cg) &&
                    double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var cb))
                {
                    return (Math.Clamp(cr, 0, 1), Math.Clamp(cg, 0, 1), Math.Clamp(cb, 0, 1));
                }
            }
        }

        if (text.StartsWith('#') && (text.Length == 7 || text.Length == 4))
        {
            var hex = text.Length == 4
                ? $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}"
                : text;

            return (
                Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0,
                Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0,
                Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);
        }

        // An unparseable colour must not fail a render, but it must not silently look like
        // a contrast failure either: mid grey is the least misleading answer available.
        return (0.5, 0.5, 0.5);
    }
}
