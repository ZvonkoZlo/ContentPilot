namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The gate every uploaded byte passes through. Nothing reaches storage unexamined.
/// <para>
/// Re-encoding is the security control that does the most work: it strips EXIF, GPS,
/// embedded colour profiles, appended payloads and polyglot files in one step, because the
/// output is written from decoded pixels rather than copied from the input. A file that
/// cannot be decoded as an image never becomes one.
/// </para>
/// </summary>
public interface IImageIngestor
{
    Task<IngestedImage> IngestAsync(
        Stream source,
        string fileName,
        IngestOptions? options = null,
        CancellationToken ct = default);
}

public sealed record IngestOptions
{
    public static readonly IngestOptions Default = new();

    /// <summary>Refuses a file before it is decoded, so a bomb cannot be expanded first.</summary>
    public long MaxBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>
    /// A decompression bomb is small on disk and enormous in memory. The pixel count is the
    /// real limit; the byte count alone would not catch it.
    /// </summary>
    public long MaxPixels { get; init; } = 50_000_000;

    public int MinWidth { get; init; } = 32;

    public int MinHeight { get; init; } = 32;

    /// <summary>Oversized originals are scaled down; the asset library is not an archive.</summary>
    public int MaxDimension { get; init; } = 4096;

    /// <summary>Produced alongside every original for the library UI.</summary>
    public int ThumbnailWidth { get; init; } = 480;

    /// <summary>
    /// SVG is XML that can carry scripts and external references, and it would be loaded
    /// into the renderer's browser. It is sanitised and rasterised on ingest, never stored.
    /// </summary>
    public bool AllowVectorInput { get; init; } = true;

    /// <summary>Transparency is meaningful for logos and destructive for photographs.</summary>
    public bool PreserveTransparency { get; init; } = true;
}

public sealed record IngestedImage
{
    public required byte[] Bytes { get; init; }

    public required string MediaType { get; init; }

    public required string Extension { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>Used later to spot a re-upload of the same picture under a different name.</summary>
    public string? PerceptualHash { get; init; }

    public IReadOnlyList<string> DominantColors { get; init; } = [];

    public byte[]? ThumbnailBytes { get; init; }

    public int ThumbnailWidth { get; init; }

    public int ThumbnailHeight { get; init; }

    /// <summary>True when the source was vector art that was flattened on the way in.</summary>
    public bool WasRasterised { get; init; }

    /// <summary>Metadata that was present and has been discarded. Shown to the operator.</summary>
    public IReadOnlyList<string> StrippedMetadata { get; init; } = [];
}

/// <summary>
/// The upload is not something this platform will accept. Always the caller's problem, so
/// it maps to 400 and carries a message a person can act on.
/// </summary>
public sealed class UnsupportedAssetException(string message) : Exception(message);
