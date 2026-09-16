using ContentPilot.Rendering.Contracts;
using ImageMagick;
using ImageMagick.Drawing;

namespace ContentPilot.RendererTests;

/// <summary>
/// Synthetic stand-ins for the real Appointso asset library, generated deterministically so
/// the suite needs no binaries in source control and produces identical bytes on every run.
/// <para>
/// They are placeholders for <em>testing the pipeline</em>, never for judging design. The
/// "would I publish this" question in the Phase 2 acceptance criteria can only be answered
/// with real screenshots and a real logo.
/// </para>
/// </summary>
public static class FixtureAssets
{
    /// <summary>
    /// A plausible booking-app screen: header, service rows, a highlighted slot grid. Tall
    /// and detailed enough that downscaling it genuinely loses high-frequency information,
    /// which is exactly the condition the fidelity check has to survive.
    /// </summary>
    public static ImagePayload ProductScreenshot(uint width = 828, uint height = 1792)
    {
        using var image = new MagickImage(new MagickColor("#FFFFFF"), width, height);
        var drawables = new Drawables();

        // Header
        drawables.FillColor(new MagickColor("#6C4CF1"))
            .Rectangle(0, 0, width, 220);

        drawables.FillColor(MagickColors.White)
            .Font("DejaVu Sans")
            .FontPointSize(46)
            .Text(48, 130, "Book an appointment");

        drawables.FillColor(new MagickColor("#D9CEFF"))
            .FontPointSize(30)
            .Text(48, 178, "Glow Studio - Zagreb");

        // Service rows
        var rowTop = 280.0;

        foreach (var (label, price) in new[]
                 {
                     ("Haircut and styling", "35 EUR"),
                     ("Manicure", "22 EUR"),
                     ("Gel nails", "40 EUR"),
                     ("Facial treatment", "55 EUR"),
                 })
        {
            drawables.FillColor(new MagickColor("#F4F5F7"))
                .RoundRectangle(40, rowTop, width - 40, rowTop + 132, 18, 18);

            drawables.FillColor(new MagickColor("#6C4CF1"))
                .RoundRectangle(64, rowTop + 30, 136, rowTop + 102, 14, 14);

            drawables.FillColor(new MagickColor("#1B1F27"))
                .FontPointSize(34)
                .Text(168, rowTop + 62, label);

            drawables.FillColor(new MagickColor("#6B7280"))
                .FontPointSize(26)
                .Text(168, rowTop + 100, "45 min");

            drawables.FillColor(new MagickColor("#1B1F27"))
                .FontPointSize(30)
                .Text(width - 200, rowTop + 78, price);

            rowTop += 156;
        }

        // Slot grid
        drawables.FillColor(new MagickColor("#1B1F27"))
            .FontPointSize(34)
            .Text(48, rowTop + 60, "Thursday, 12 March");

        var slotTop = rowTop + 100;

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var x = 44 + col * 250.0;
                var y = slotTop + row * 108.0;
                var selected = row == 1 && col == 1;

                drawables.FillColor(new MagickColor(selected ? "#6C4CF1" : "#FFFFFF"))
                    .StrokeColor(new MagickColor(selected ? "#6C4CF1" : "#DDE1E6"))
                    .StrokeWidth(2)
                    .RoundRectangle(x, y, x + 226, y + 84, 14, 14);

                drawables.StrokeColor(MagickColors.Transparent)
                    .FillColor(new MagickColor(selected ? "#FFFFFF" : "#374151"))
                    .FontPointSize(30)
                    .Text(x + 62, y + 54, $"{9 + row}:{(col == 0 ? "00" : col == 1 ? "30" : "45")}");
            }
        }

        // Primary action
        drawables.FillColor(new MagickColor("#6C4CF1"))
            .RoundRectangle(44, height - 170, width - 44, height - 70, 22, 22);

        drawables.FillColor(MagickColors.White)
            .FontPointSize(36)
            .Text(width / 2.0 - 90, height - 105, "Confirm");

        drawables.Draw(image);

        return ImagePayload.FromBytes(image.ToByteArray(MagickFormat.Png), "image/png");
    }

    /// <summary>
    /// A sparse product screen like the first live campaign upload: large flat regions and
    /// repeated low-contrast rows. DCT hashes are deliberately fragile on this shape because
    /// many low-frequency coefficients sit close to the median.
    /// </summary>
    public static ImagePayload SparseProductScreenshot(uint width = 900, uint height = 1600)
    {
        using var image = new MagickImage(MagickColors.White, width, height);
        var drawables = new Drawables()
            .FillColor(new MagickColor("#6C4CF1"))
            .Rectangle(0, 0, width, height / 8.0);

        var rowHeight = height / 10.0;

        for (var row = 0; row < 8; row++)
        {
            var top = height / 7.0 + row * rowHeight;
            drawables.FillColor(new MagickColor("#F4F5F7"))
                .Rectangle(0, top, width, top + rowHeight * 0.78);
        }

        drawables.Draw(image);
        image.Format = MagickFormat.Jpeg;
        image.Quality = 85;

        return ImagePayload.FromBytes(image.ToByteArray(), "image/jpeg");
    }

    /// <summary>A wordmark with a mark, at a wide aspect so distortion is easy to spot.</summary>
    public static ImagePayload Logo()
    {
        using var image = new MagickImage(MagickColors.Transparent, 600, 160);

        new Drawables()
            .FillColor(new MagickColor("#FFFFFF"))
            .RoundRectangle(10, 40, 90, 120, 20, 20)
            .FillColor(new MagickColor("#6C4CF1"))
            .Ellipse(50, 80, 22, 22, 0, 360)
            .FillColor(new MagickColor("#FFFFFF"))
            .Font("DejaVu Sans")
            .FontPointSize(72)
            .Text(112, 106, "Appointso")
            .Draw(image);

        return ImagePayload.FromBytes(image.ToByteArray(MagickFormat.Png), "image/png");
    }

    /// <summary>Stands in for a generated background: soft, out of focus, no detail to lose.</summary>
    public static ImagePayload Background(uint width = 1080, uint height = 1350)
    {
        using var image = new MagickImage(new MagickColor("#241C4A"), width, height);

        new Drawables()
            .FillColor(new MagickColor("#4B32B8"))
            .Ellipse(width * 0.25, height * 0.22, width * 0.55, height * 0.35, 0, 360)
            .FillColor(new MagickColor("#8B5CF6"))
            .Ellipse(width * 0.82, height * 0.68, width * 0.42, height * 0.3, 0, 360)
            .Draw(image);

        image.Blur(0, 60);

        return ImagePayload.FromBytes(image.ToByteArray(MagickFormat.Jpeg), "image/jpeg");
    }

    public static BrandTokens Brand() => new()
    {
        Name = "Appointso",
        PrimaryColor = "#6C4CF1",
        SecondaryColor = "#2A2350",
        AccentColor = "#F5A8C8",
        DarkColor = "#12101F",
        LightColor = "#F7F7FB",
        HeadingFont = "Archivo",
        BodyFont = "Inter",
        CornerRadius = 16,
    };
}
