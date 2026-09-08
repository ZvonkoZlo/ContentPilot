using System.Xml;
using System.Xml.Linq;

namespace ContentPilot.Infrastructure.Assets;

/// <summary>
/// Strips an SVG down to drawing instructions.
/// <para>
/// An SVG logo is XML, and the renderer loads it into a browser. Left alone it can carry
/// scripts, event handlers, external references that phone home, and entity declarations
/// that expand into gigabytes or read local files. This works by allow-list rather than
/// block-list: anything not explicitly permitted is removed, so a construct nobody thought
/// of is dropped rather than passed through.
/// </para>
/// <para>
/// The sanitised output is only ever handed to the rasteriser, and the vector original is
/// discarded. Nothing downstream sees XML.
/// </para>
/// </summary>
public static class SvgSanitizer
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "metadata",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan",
        "linearGradient", "radialGradient", "stop",
        "clipPath", "mask", "pattern", "symbol", "marker",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "id", "class", "d", "fill", "fill-rule", "fill-opacity", "stroke", "stroke-width",
        "stroke-linecap", "stroke-linejoin", "stroke-dasharray", "stroke-opacity", "stroke-miterlimit",
        "opacity", "transform", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "points", "offset", "stop-color", "stop-opacity",
        "gradientUnits", "gradientTransform", "spreadMethod", "clip-path", "clip-rule", "mask",
        "font-family", "font-size", "font-weight", "font-style", "text-anchor", "letter-spacing",
        "dominant-baseline", "preserveAspectRatio", "version", "style", "patternUnits",
    };

    /// <summary>
    /// Returns sanitised SVG markup. Throws when the document is not usable as vector art at
    /// all — malformed XML, no root, or a payload that only makes sense as an attack.
    /// </summary>
    public static string Sanitize(string svg)
    {
        if (svg.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase) ||
            svg.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            // Entity expansion and external DTDs are the billion-laughs and XXE families.
            // There is no legitimate logo that needs them.
            throw new InvalidOperationException("SVG contains a DOCTYPE or entity declaration.");
        }

        XDocument document;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
        };

        try
        {
            using var stringReader = new StringReader(svg);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"SVG is not well-formed XML: {ex.Message}", ex);
        }

        if (document.Root is null || document.Root.Name.LocalName != "svg")
        {
            throw new InvalidOperationException("SVG has no <svg> root element.");
        }

        Scrub(document.Root);

        if (!document.Root.Descendants().Any(e => IsDrawing(e.Name.LocalName)))
        {
            throw new InvalidOperationException("SVG contains no drawable content once sanitised.");
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void Scrub(XElement element)
    {
        foreach (var child in element.Elements().ToArray())
        {
            if (!AllowedElements.Contains(child.Name.LocalName))
            {
                // script, foreignObject, image, use, animate, set, filter, style — all gone.
                child.Remove();
                continue;
            }

            Scrub(child);
        }

        foreach (var attribute in element.Attributes().ToArray())
        {
            var name = attribute.Name.LocalName;

            if (attribute.IsNamespaceDeclaration)
            {
                // Only the SVG namespace survives; xlink is how external references travel.
                if (attribute.Value != Svg.NamespaceName)
                {
                    attribute.Remove();
                }

                continue;
            }

            if (!AllowedAttributes.Contains(name))
            {
                attribute.Remove();
                continue;
            }

            if (ContainsActiveContent(attribute.Value))
            {
                attribute.Remove();
            }
        }
    }

    /// <summary>
    /// Even an allowed attribute can carry a payload: <c>fill="url(http://…)"</c> leaks a
    /// request, and <c>style</c> can hold <c>expression()</c> or a remote import.
    /// </summary>
    private static bool ContainsActiveContent(string value)
    {
        var lowered = value.ToLowerInvariant();

        return lowered.Contains("javascript:", StringComparison.Ordinal)
            || lowered.Contains("data:text/html", StringComparison.Ordinal)
            || lowered.Contains("expression(", StringComparison.Ordinal)
            || lowered.Contains("@import", StringComparison.Ordinal)
            || lowered.Contains("http://", StringComparison.Ordinal)
            || lowered.Contains("https://", StringComparison.Ordinal);
    }

    private static bool IsDrawing(string localName) => localName is
        "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "text";
}
