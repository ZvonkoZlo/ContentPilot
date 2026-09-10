using ContentPilot.Domain.Content;
using Shouldly;

namespace ContentPilot.UnitTests.Domain;

public sealed class TemplateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_snapshot_records_the_manifest_and_its_hash()
    {
        var snapshot = new TemplateVersion("phone-floating", 3, """{"templateId":"phone-floating"}""", "abc123", Now);

        snapshot.TemplateId.ShouldBe("phone-floating");
        snapshot.Version.ShouldBe(3);
        snapshot.ContentHash.ShouldBe("abc123");
    }

    [Fact]
    public void A_version_below_one_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new TemplateVersion("phone-floating", 0, "{}", "hash", Now));
    }

    [Fact]
    public void A_blank_manifest_is_refused()
    {
        Should.Throw<ArgumentException>(() =>
            new TemplateVersion("phone-floating", 1, "   ", "hash", Now));
    }
}

public sealed class CreativeSpecTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_spec_pins_the_template_version_it_was_built_against()
    {
        var templateVersionId = Guid.CreateVersion7();
        var spec = new CreativeSpec(
            Guid.CreateVersion7(), Guid.CreateVersion7(), attempt: 1, templateVersionId,
            specJson: """{"templateId":"phone-floating"}""", specHash: "hash123", createdAt: Now);

        spec.TemplateVersionId.ShouldBe(templateVersionId);
        spec.Attempt.ShouldBe(1);
    }

    [Fact]
    public void An_attempt_below_one_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CreativeSpec(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, Guid.CreateVersion7(), "{}", "hash", Now));
    }
}

public sealed class ContentAssetTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_image_asset_records_its_dimensions()
    {
        var asset = new ContentAsset(
            Guid.CreateVersion7(), Guid.CreateVersion7(), attempt: 1, ContentAssetKind.Image,
            storageKey: "t/abc/campaigns/1/post-01/image.png", mediaType: "image/png",
            sha256: "deadbeef", bytes: 412_880, createdAt: Now, width: 1080, height: 1350);

        asset.Kind.ShouldBe(ContentAssetKind.Image);
        asset.Width.ShouldBe(1080);
        asset.Height.ShouldBe(1350);
    }

    [Fact]
    public void A_caption_style_asset_needs_no_dimensions()
    {
        var asset = new ContentAsset(
            Guid.CreateVersion7(), Guid.CreateVersion7(), attempt: 1, ContentAssetKind.CarouselSlide,
            storageKey: "t/abc/campaigns/1/post-01/slide-01.png", mediaType: "image/png",
            sha256: "deadbeef", bytes: 200_000, createdAt: Now, metaJson: """{"ordinal":1}""");

        asset.Width.ShouldBe(0);
        asset.MetaJson.ShouldBe("""{"ordinal":1}""");
    }

    [Fact]
    public void A_blank_storage_key_is_refused()
    {
        Should.Throw<ArgumentException>(() =>
            new ContentAsset(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, ContentAssetKind.Image, "  ", "image/png", "hash", 1, Now));
    }
}
