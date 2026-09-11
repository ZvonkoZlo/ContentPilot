using System.Globalization;
using System.Text;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;

namespace ContentPilot.Renderer.Video;

/// <summary>Builds isolated reel-layer documents with the same fonts, tokens and guards as static renders.</summary>
public sealed class ReelDocumentBuilder(FontLibrary fonts)
{
    public string Build(ReelSpec spec, ReelTemplateManifest manifest, string componentHtml, bool transparent)
    {
        var brand = spec.Brand;
        var foreground = spec.ColorScheme == ColorScheme.LightOnDark ? "#FFFFFF" : brand.DarkColor;
        var ground = spec.ColorScheme == ColorScheme.LightOnDark ? brand.DarkColor : brand.LightColor;
        var muted = spec.ColorScheme == ColorScheme.LightOnDark
            ? "rgba(255,255,255,0.72)"
            : "rgba(16,20,24,0.68)";
        var frameBackground = transparent ? "transparent" : ground;
        var html = new StringBuilder(1024 * 1024);

        html.Append("<!doctype html><html lang=\"").Append(Escape(spec.Language))
            .Append("\"><head><meta charset=\"utf-8\"><style>").Append(fonts.FaceCss).Append("</style><style>");

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

        html.Append("*,*::before,*::after{box-sizing:border-box;animation:none!important;")
            .Append("transition:none!important;caret-color:transparent!important}")
            .Append("html,body{margin:0;padding:0;background:transparent}")
            .Append("#frame{position:relative;overflow:hidden;width:540px;height:960px;background:")
            .Append(frameBackground)
            .Append(";color:var(--fg);font-family:var(--font-body);-webkit-font-smoothing:antialiased;")
            .Append("text-rendering:geometricPrecision}")
            .Append("[data-immutable]{filter:none!important;mix-blend-mode:normal!important;opacity:1!important}")
            .Append("[data-immutable] img{display:block;width:100%;height:100%;object-fit:contain!important;")
            .Append("object-position:center!important;filter:none!important}")
            .Append("[data-slot]{white-space:pre-wrap;overflow-wrap:break-word;hyphens:none}")
            .Append("</style></head><body><div id=\"").Append(DocumentBuilder.FrameElementId).Append("\">")
            .Append(componentHtml)
            .Append("</div></body></html>");

        return html.ToString();
    }

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

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);
}
