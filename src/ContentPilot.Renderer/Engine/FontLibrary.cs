using System.Text;

namespace ContentPilot.Renderer.Engine;

/// <summary>
/// Fonts are embedded as data URIs and the page's network is blocked, so a font either
/// loads from this library or the render fails loudly. The alternative — a silent fallback
/// to a system face — produces a subtly wrong image that QA may well pass.
/// <para>
/// Every family here ships latin and latin-ext, so Croatian diacritics (č ć ž š đ) render
/// rather than falling back to tofu.
/// </para>
/// </summary>
public sealed class FontLibrary
{
    private readonly string _faceCss;

    public FontLibrary(string fontDirectory, ILogger<FontLibrary> logger)
    {
        if (!Directory.Exists(fontDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Font directory '{fontDirectory}' is missing. The renderer cannot start without embedded fonts.");
        }

        var files = Directory.GetFiles(fontDirectory, "*.woff2").OrderBy(f => f).ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"No .woff2 files in '{fontDirectory}'. Embedded fonts are what make renders deterministic.");
        }

        var css = new StringBuilder();
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            // Filenames are the contract: <family>-<weight>-<subset>.woff2
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');

            if (parts.Length < 3 || !int.TryParse(parts[1], out var weight))
            {
                logger.LogWarning("Skipping font {File}: expected <family>-<weight>-<subset>.woff2.", file);
                continue;
            }

            var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
            var subset = string.Join('-', parts[2..]);
            var base64 = Convert.ToBase64String(File.ReadAllBytes(file));

            families.Add(family);

            css.Append("@font-face{font-family:'").Append(family)
               .Append("';font-style:normal;font-weight:").Append(weight)
               .Append(";font-display:block;src:url(data:font/woff2;base64,")
               .Append(base64)
               .Append(") format('woff2');")
               .Append(UnicodeRangeFor(subset))
               .Append("}\n");
        }

        Families = families.ToArray();
        _faceCss = css.ToString();

        logger.LogInformation(
            "Embedded {FileCount} font files covering {Families}.", files.Length, string.Join(", ", Families));
    }

    public IReadOnlyList<string> Families { get; }

    public string FaceCss => _faceCss;

    public bool Has(string family) =>
        Families.Contains(family, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restricting each subset to its range is what lets the browser pick the latin-ext
    /// file only when a diacritic actually appears, instead of downloading everything.
    /// </summary>
    private static string UnicodeRangeFor(string subset) => subset switch
    {
        "latin" =>
            "unicode-range:U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC," +
            "U+0304,U+0308,U+0329,U+2000-206F,U+2074,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD;",
        "latin-ext" =>
            "unicode-range:U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308," +
            "U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF;",
        _ => string.Empty,
    };
}
