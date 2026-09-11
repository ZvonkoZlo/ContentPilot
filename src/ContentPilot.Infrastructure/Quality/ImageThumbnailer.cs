using ContentPilot.Rendering.Contracts;
using ImageMagick;

namespace ContentPilot.Infrastructure.Quality;

/// <summary>
/// The 150 px-wide render §9 asks VisualQA to judge alongside the full image — thumbnail
/// legibility is how social content is actually consumed, and this is the only place that
/// needs to know how to produce one.
/// </summary>
public static class ImageThumbnailer
{
    public const int ThumbnailWidth = 150;

    public static ImagePayload Create(ImagePayload source)
    {
        using var image = new MagickImage(source.ToBytes());

        if (image.Width > ThumbnailWidth)
        {
            image.Resize(new MagickGeometry((uint)ThumbnailWidth, 0));
        }

        image.Format = MagickFormat.Png;

        return ImagePayload.FromBytes(image.ToByteArray(), "image/png");
    }
}
