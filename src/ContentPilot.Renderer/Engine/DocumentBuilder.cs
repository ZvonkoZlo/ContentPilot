using System.Globalization;
using System.Text;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Engine;

/// <summary>
/// Assembles the document the browser screenshots: embedded fonts, brand tokens as CSS
/// custom properties, a fixed-size frame element, and the template's own markup.
/// <para>
/// Everything here exists to make the output a pure function of the request. No network,
/// no animation, no time, no randomness — the same request renders the same bytes.
/// </para>
/// </summary>
public sealed class DocumentBuilder(FontLibrary fonts)
{
    public const string FrameElementId = "frame";

    public string Build(RenderImageRequest request, TemplateManifest manifest, string templateHtml)
    {
        var (cssWidth, cssHeight) = request.AspectRatio.CssDimensions();
        var brand = request.Brand;
        var scheme = request.ColorScheme;

        var foreground = scheme == ColorScheme.LightOnDark ? "#FFFFFF" : brand.DarkColor;
        var ground = scheme == ColorScheme.LightOnDark ? brand.DarkColor : brand.LightColor;
        var muted = scheme == ColorScheme.LightOnDark ? "rgba(255,255,255,0.72)" : "rgba(16,20,24,0.68)";

        var html = new StringBuilder(1024 * 1024);

        html.Append("<!doctype html><html lang=\"").Append(Escape(request.Language)).Append("\"><head><meta charset=\"utf-8\">");
        html.Append("<style>").Append(fonts.FaceCss).Append("</style>");
        html.Append("<style>");

        html.Append(":root{")
            .Append("--brand-primary:").Append(Css(brand.PrimaryColor)).Append(';')
            .Append("--brand-secondary:").Append(Css(brand.SecondaryColor ?? brand.PrimaryColor)).Append(';')
            .Append("--brand-accent:").Append(Css(brand.AccentColor ?? brand.PrimaryColor)).Append(';')
            .Append("--brand-dark:").Append(Css(brand.DarkColor)).Append(';')
            .Append("--brand-light:").Append(Css(brand.LightColor)).Append(';')
            .Append("--fg:").Append(foreground).Append(';')
            .Append("--fg-muted:").Append(muted).Append(';')
            .Append("--ground:").Append(ground).Append(';')
            .Append("--radius:").Append(Num(brand.CornerRadius)).Append("px;")
            .Append("--font-heading:'").Append(Css(brand.HeadingFont)).Append("',Georgia,serif;")
            .Append("--font-body:'").Append(Css(brand.BodyFont)).Append("',system-ui,sans-serif;")
            .Append("--safe-top:").Append(Num(manifest.SafeAreas.Top * 100)).Append("%;")
            .Append("--safe-bottom:").Append(Num(manifest.SafeAreas.Bottom * 100)).Append("%;")
            .Append("--safe-x:").Append(Num(manifest.SafeAreas.Left * 100)).Append("%;")
            .Append('}');

        // Determinism guards. Any of these leaking into a template makes goldens flap.
        html.Append("*,*::before,*::after{box-sizing:border-box;animation:none!important;")
            .Append("transition:none!important;caret-color:transparent!important}");

        html.Append("html,body{margin:0;padding:0;background:").Append(ground).Append("}");

        html.Append("#").Append(FrameElementId).Append("{position:relative;overflow:hidden;")
            .Append("width:").Append(cssWidth).Append("px;height:").Append(cssHeight).Append("px;")
            .Append("background:var(--ground);color:var(--fg);font-family:var(--font-body);")
            .Append("-webkit-font-smoothing:antialiased;text-rendering:geometricPrecision}");

        // Immutable slots may be scaled uniformly and nothing else. Enforced in CSS as well
        // as in validation, so a template author cannot tint or blur a product screenshot.
        html.Append("[data-immutable]{filter:none!important;mix-blend-mode:normal!important;opacity:1!important}");
        // object-fit is forced, not merely defaulted: "cover" crops, and a cropped
        // screenshot is an altered screenshot. Contain guarantees the browser scales it
        // uniformly, which is what makes the fidelity check a comparison of like with like.
        html.Append("[data-immutable] img{display:block;width:100%;height:100%;")
            .Append("object-fit:contain!important;object-position:center!important;filter:none!important}");

        html.Append("[data-slot]{white-space:pre-wrap;overflow-wrap:break-word;hyphens:none}");

        if (request.Options.DebugOverlay)
        {
            html.Append("[data-slot]{outline:1px dashed rgba(255,0,128,.9)}")
                .Append("[data-asset-slot]{outline:1px dashed rgba(0,200,255,.9)}")
                .Append("#").Append(FrameElementId).Append("::after{content:'';position:absolute;inset:")
                .Append("var(--safe-top) var(--safe-x) var(--safe-bottom) var(--safe-x);")
                .Append("outline:1px solid rgba(255,196,0,.8);pointer-events:none}");
        }

        html.Append("</style></head><body><div id=\"").Append(FrameElementId).Append("\">");
        html.Append(templateHtml);
        html.Append("</div></body></html>");

        return html.ToString();
    }

    /// <summary>
    /// Colours and family names reach CSS from tenant-editable data, so they are constrained
    /// to a conservative character set rather than trusted. A template cannot be turned into
    /// an injection point by a brand profile.
    /// </summary>
    private static string Css(string value)
    {
        var cleaned = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '#' or '(' or ')' or ',' or '.' or '%' or ' ' or '-')
            {
                cleaned.Append(c);
            }
        }

        return cleaned.ToString();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
