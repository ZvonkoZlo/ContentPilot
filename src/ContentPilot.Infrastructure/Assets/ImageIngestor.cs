using System.Security.Cryptography;
using System.Text;
using ContentPilot.Application.Abstractions;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Assets;

/// <summary>
/// Validates, re-encodes and measures every uploaded image.
/// <para>
/// The order is deliberate. Size is checked before anything is decoded, so a decompression
/// bomb is refused while it is still small. The format is decided by magic bytes rather
/// than the filename, because an extension is a claim by the uploader. Only then is the
/// file decoded, and the stored bytes are written out from those decoded pixels — which is
/// what makes every trailing payload, EXIF block and polyglot trick disappear.
/// </para>
/// </summary>
public sealed class ImageIngestor(ILogger<ImageIngestor> logger) : IImageIngestor
{
    public async Task<IngestedImage> IngestAsync(
        Stream source,
        string fileName,
        IngestOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? IngestOptions.Default;

        var raw = await ReadBoundedAsync(source, opts.MaxBytes, ct);
        var format = DetectFormat(raw);

        if (format == SourceFormat.Unknown)
        {
            throw new UnsupportedAssetException(
                $"'{fileName}' is not a PNG, JPEG, WebP, GIF or SVG. The file signature does not match any supported image format.");
        }

        var rasterised = false;

        if (format == SourceFormat.Svg)
        {
            if (!opts.AllowVectorInput)
            {
                throw new UnsupportedAssetException("Vector uploads are disabled for this asset kind.");
            }

            raw = RasteriseSvg(raw, opts, fileName);
            rasterised = true;
        }

        using var image = LoadGuarded(raw, opts, fileName);

        if (image.Width < opts.MinWidth || image.Height < opts.MinHeight)
        {
            throw new UnsupportedAssetException(
                $"'{fileName}' is {image.Width}x{image.Height}; the minimum is {opts.MinWidth}x{opts.MinHeight}. " +
                "An asset this small will look soft at posting size.");
        }

        var stripped = DescribeMetadata(image);

        // Everything that is not pixels goes. EXIF carries GPS coordinates and camera
        // serial numbers straight into a published image, and profiles change how colours
        // render downstream.
        image.Strip();

        if (Math.Max(image.Width, image.Height) > opts.MaxDimension)
        {
            var geometry = new MagickGeometry((uint)opts.MaxDimension, (uint)opts.MaxDimension);
            image.FilterType = FilterType.Lanczos;
            image.Resize(geometry);

            logger.LogInformation(
                "Downscaled {FileName} to {Width}x{Height} on ingest.", fileName, image.Width, image.Height);
        }

        var hasAlpha = opts.PreserveTransparency && image.HasAlpha;
        var outputFormat = hasAlpha ? MagickFormat.Png : MagickFormat.Jpeg;

        if (!hasAlpha)
        {
            image.Quality = 92;
            image.Alpha(AlphaOption.Remove);
        }

        image.ColorSpace = ColorSpace.sRGB;
        image.Format = outputFormat;

        var bytes = image.ToByteArray();

        return new IngestedImage
        {
            Bytes = bytes,
            MediaType = hasAlpha ? "image/png" : "image/jpeg",
            Extension = hasAlpha ? "png" : "jpg",
            Width = (int)image.Width,
            Height = (int)image.Height,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            PerceptualHash = ComputePerceptualHash(image),
            DominantColors = ExtractDominantColors(image),
            ThumbnailBytes = RenderThumbnail(image, opts, out var thumbWidth, out var thumbHeight),
            ThumbnailWidth = thumbWidth,
            ThumbnailHeight = thumbHeight,
            WasRasterised = rasterised,
            StrippedMetadata = stripped,
        };
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> plus one, so an oversized upload is refused
    /// without ever being held in memory in full.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(chunk, ct);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (total > maxBytes)
            {
                throw new UnsupportedAssetException(
                    $"The upload exceeds the {maxBytes / 1024 / 1024} MB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (total == 0)
        {
            throw new UnsupportedAssetException("The upload is empty.");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the dimensions from the header before decoding. A 200 KB PNG can declare
    /// 60000x60000 and cost 14 GB to decode; checking the header first is the only defence
    /// that works before the damage is done.
    /// </summary>
    private static MagickImage LoadGuarded(byte[] raw, IngestOptions opts, string fileName)
    {
        try
        {
            var info = new MagickImageInfo(raw);

            if ((long)info.Width * info.Height > opts.MaxPixels)
            {
                throw new UnsupportedAssetException(
                    $"'{fileName}' declares {info.Width}x{info.Height}, which exceeds the " +
                    $"{opts.MaxPixels / 1_000_000} megapixel limit.");
            }
        }
        catch (MagickException ex)
        {
            throw new UnsupportedAssetException($"'{fileName}' could not be read as an image: {ex.Message}");
        }

        try
        {
            return new MagickImage(raw);
        }
        catch (MagickException ex)
        {
            throw new UnsupportedAssetException($"'{fileName}' could not be decoded: {ex.Message}");
        }
    }

    private byte[] RasteriseSvg(byte[] raw, IngestOptions opts, string fileName)
    {
        string sanitised;

        try
        {
            sanitised = SvgSanitizer.Sanitize(Encoding.UTF8.GetString(raw));
        }
        catch (InvalidOperationException ex)
        {
            throw new UnsupportedAssetException($"'{fileName}' was rejected: {ex.Message}");
        }

        try
        {
            var settings = new MagickReadSettings
            {
                Format = MagickFormat.Svg,
                BackgroundColor = MagickColors.Transparent,
                // Rasterise generously: a logo is scaled down into slots, never up.
                Width = (uint)opts.MaxDimension,
            };

            using var image = new MagickImage(Encoding.UTF8.GetBytes(sanitised), settings);
            image.Format = MagickFormat.Png;

            logger.LogInformation("Rasterised {FileName} to {Width}x{Height}.", fileName, image.Width, image.Height);

            return image.ToByteArray();
        }
        catch (MagickException ex)
        {
            throw new UnsupportedAssetException(
                $"'{fileName}' passed sanitisation but could not be rasterised ({ex.Message}). " +
                "Upload a PNG export instead.");
        }
    }

    /// <summary>
    /// Magic bytes, never the extension. A renamed executable claims to be a PNG and this is
    /// the check that disagrees.
    /// </summary>
    internal static SourceFormat DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return SourceFormat.Unknown;
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return SourceFormat.Png;
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return SourceFormat.Jpeg;
        }

        if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return SourceFormat.WebP;
        }

        if (bytes[..3].SequenceEqual("GIF"u8))
        {
            return SourceFormat.Gif;
        }

        // SVG has no signature, so the opening markup is the only tell. Look at a bounded
        // prefix rather than the whole file.
        var prefix = Encoding.UTF8.GetString(bytes[..Math.Min(512, bytes.Length)]).TrimStart();

        if (prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
             prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
        {
            return SourceFormat.Svg;
        }

        return SourceFormat.Unknown;
    }

    private static IReadOnlyList<string> DescribeMetadata(IMagickImage<ushort> image)
    {
        var found = new List<string>();

        if (image.GetExifProfile() is not null)
        {
            found.Add("EXIF");
        }

        if (image.GetIptcProfile() is not null)
        {
            found.Add("IPTC");
        }

        if (image.GetXmpProfile() is not null)
        {
            found.Add("XMP");
        }

        if (image.GetColorProfile() is not null)
        {
            found.Add("ICC colour profile");
        }

        return found;
    }

    private string? ComputePerceptualHash(IMagickImage<ushort> image)
    {
        try
        {
            return image.PerceptualHash()?.ToString();
        }
        catch (MagickException ex)
        {
            // Never fail an upload over a nice-to-have measurement.
            logger.LogDebug(ex, "Perceptual hash unavailable for this image.");
            return null;
        }
    }

    /// <summary>
    /// Quantises to a handful of colours. Used later to pick a background that sits well
    /// against an asset, and to sanity-check a logo against the declared brand palette.
    /// </summary>
    private static IReadOnlyList<string> ExtractDominantColors(IMagickImage<ushort> image)
    {
        try
        {
            using var sample = (MagickImage)image.Clone();
            sample.Resize(new MagickGeometry(160, 160) { IgnoreAspectRatio = false });
            sample.Quantize(new QuantizeSettings { Colors = 5, DitherMethod = DitherMethod.No });

            return sample.Histogram()
                .OrderByDescending(entry => entry.Value)
                .Select(entry => entry.Key.ToHexString())
                .Where(hex => hex.Length >= 7)
                .Select(hex => hex[..7].ToUpperInvariant())
                .Distinct()
                .Take(5)
                .ToArray();
        }
        catch (MagickException)
        {
            return [];
        }
    }

    private static byte[]? RenderThumbnail(IMagickImage<ushort> image, IngestOptions opts, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (image.Width <= opts.ThumbnailWidth)
        {
            return null;
        }

        try
        {
            using var thumb = (MagickImage)image.Clone();
            thumb.FilterType = FilterType.Lanczos;
            thumb.Resize(new MagickGeometry((uint)opts.ThumbnailWidth, (uint)(opts.ThumbnailWidth * 4)));
            thumb.Strip();
            thumb.Format = image.HasAlpha ? MagickFormat.Png : MagickFormat.Jpeg;

            width = (int)thumb.Width;
            height = (int)thumb.Height;

            return thumb.ToByteArray();
        }
        catch (MagickException)
        {
            return null;
        }
    }
}

internal enum SourceFormat
{
    Unknown,
    Png,
    Jpeg,
    WebP,
    Gif,
    Svg,
}
