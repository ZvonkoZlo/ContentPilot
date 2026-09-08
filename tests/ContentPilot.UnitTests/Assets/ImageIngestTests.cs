using System.Text;
using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Assets;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ContentPilot.UnitTests.Assets;

/// <summary>
/// Upload is the one place an outsider chooses the bytes. Everything here is a hostile
/// input the ingestor has to refuse or defuse.
/// </summary>
public sealed class ImageIngestTests
{
    private static readonly ImageIngestor Ingestor = new(NullLogger<ImageIngestor>.Instance);

    private static Task<IngestedImage> IngestAsync(byte[] bytes, string fileName, IngestOptions? options = null) =>
        Ingestor.IngestAsync(new MemoryStream(bytes), fileName, options);

    private static byte[] Png(uint width = 400, uint height = 400, bool alpha = false)
    {
        using var image = new MagickImage(alpha ? MagickColors.Transparent : new MagickColor("#3366CC"), width, height);

        // Flat colour compresses to almost nothing and can look like a bomb; give it detail.
        new Drawables()
            .FillColor(MagickColors.White).Rectangle(10, 10, width / 2.0, height / 2.0)
            .FillColor(new MagickColor("#DD3355")).Ellipse(width / 2.0, height / 2.0, 60, 40, 0, 360)
            .Draw(image);

        image.Format = MagickFormat.Png;

        return image.ToByteArray();
    }

    [Fact]
    public async Task A_normal_png_is_accepted_and_measured()
    {
        var result = await IngestAsync(Png(), "screenshot.png");

        result.Width.ShouldBe(400);
        result.Height.ShouldBe(400);
        result.Sha256.Length.ShouldBe(64);
        result.DominantColors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_renamed_executable_is_refused()
    {
        // MZ header: a Windows binary claiming to be an image. The extension is a claim by
        // the uploader; the signature is the fact.
        var exe = new byte[2048];
        exe[0] = 0x4D;
        exe[1] = 0x5A;

        var ex = await Should.ThrowAsync<UnsupportedAssetException>(() => IngestAsync(exe, "logo.png"));

        ex.Message.ShouldContain("signature");
    }

    [Fact]
    public async Task A_text_file_with_an_image_extension_is_refused() =>
        await Should.ThrowAsync<UnsupportedAssetException>(
            () => IngestAsync(Encoding.UTF8.GetBytes(new string('a', 4096)), "photo.jpg"));

    [Fact]
    public async Task An_empty_upload_is_refused() =>
        await Should.ThrowAsync<UnsupportedAssetException>(() => IngestAsync([], "empty.png"));

    [Fact]
    public async Task An_oversized_upload_is_refused_before_it_is_decoded()
    {
        var options = IngestOptions.Default with { MaxBytes = 1024 };

        var ex = await Should.ThrowAsync<UnsupportedAssetException>(
            () => IngestAsync(Png(800, 800), "big.png", options));

        ex.Message.ShouldContain("limit");
    }

    [Fact]
    public async Task A_decompression_bomb_is_refused_on_its_declared_dimensions()
    {
        // Small on disk, enormous in memory. Only the header check catches this before the
        // damage is done, which is why dimensions are read before decoding.
        var options = IngestOptions.Default with { MaxPixels = 100_000 };

        var ex = await Should.ThrowAsync<UnsupportedAssetException>(
            () => IngestAsync(Png(2000, 2000), "bomb.png", options));

        ex.Message.ShouldContain("megapixel");
    }

    [Fact]
    public async Task A_tiny_image_is_refused_with_a_reason_a_person_can_act_on()
    {
        var ex = await Should.ThrowAsync<UnsupportedAssetException>(() => IngestAsync(Png(16, 16), "tiny.png"));

        ex.Message.ShouldContain("minimum");
    }

    [Fact]
    public async Task Exif_is_stripped_by_re_encoding()
    {
        using var source = new MagickImage(new MagickColor("#883322"), 600, 400);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.Software, "definitely-not-a-camera");
        source.SetProfile(exif);
        source.Format = MagickFormat.Jpeg;

        var result = await IngestAsync(source.ToByteArray(), "holiday.jpg");

        result.StrippedMetadata.ShouldContain("EXIF");

        using var stored = new MagickImage(result.Bytes);
        stored.GetExifProfile().ShouldBeNull("GPS coordinates must never survive into a published image.");
    }

    [Fact]
    public async Task Trailing_data_after_a_valid_image_does_not_survive()
    {
        var png = Png();
        var polyglot = png.Concat(Encoding.UTF8.GetBytes("<?php system($_GET[0]); ?>")).ToArray();

        var result = await IngestAsync(polyglot, "innocent.png");

        // The output is written from decoded pixels, so an appended payload has nowhere to go.
        Encoding.UTF8.GetString(result.Bytes).ShouldNotContain("php");

        // Not a size comparison: a re-encode is free to be larger than the input. What
        // proves the bytes were rebuilt rather than copied through is that they differ at
        // all, while the picture itself is unchanged.
        result.Bytes.ShouldNotBe(polyglot);
        result.Width.ShouldBe(400);
        result.Height.ShouldBe(400);
    }

    [Fact]
    public async Task An_oversized_original_is_scaled_down_rather_than_rejected()
    {
        var options = IngestOptions.Default with { MaxDimension = 256 };

        var result = await IngestAsync(Png(1200, 900), "large.png", options);

        Math.Max(result.Width, result.Height).ShouldBeLessThanOrEqualTo(256);
    }

    [Fact]
    public async Task Transparency_is_preserved_for_logos_and_dropped_for_photographs()
    {
        var logo = await IngestAsync(Png(400, 200, alpha: true), "logo.png",
            IngestOptions.Default with { PreserveTransparency = true, MinHeight = 100 });

        var photo = await IngestAsync(Png(400, 200, alpha: true), "photo.png",
            IngestOptions.Default with { PreserveTransparency = false, MinHeight = 100 });

        logo.MediaType.ShouldBe("image/png");
        photo.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task The_same_bytes_always_hash_the_same()
    {
        var png = Png();

        var first = await IngestAsync(png, "a.png");
        var second = await IngestAsync(png, "b.png");

        // Content addressing depends on this: a re-upload under a different name must
        // resolve to the object already stored.
        second.Sha256.ShouldBe(first.Sha256);
    }
}
