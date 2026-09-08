using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Common;
using ContentPilot.Domain.Tenancy;
using Shouldly;

namespace ContentPilot.UnitTests.Domain;

public sealed class TenantTests
{
    [Fact]
    public void A_tenant_starts_active_with_default_limits()
    {
        var tenant = new Tenant("Appointso", "appointso");

        tenant.IsActive.ShouldBeTrue();
        tenant.Limits.MaxQualityAttemptsPerItem.ShouldBe(3);
        tenant.Limits.MaxStepsPerItem.ShouldBe(40);
        tenant.Limits.MaxRunDuration.ShouldBe(TimeSpan.FromMinutes(45));
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("under_score")]
    [InlineData("slash/es")]
    public void A_slug_must_be_storage_and_url_safe(string slug)
    {
        // Slugs end up in object storage keys and URLs; a permissive one is a path
        // traversal waiting to happen.
        if (slug == "UPPER")
        {
            new Tenant("x", slug).Slug.ShouldBe("upper");
            return;
        }

        Should.Throw<ArgumentException>(() => new Tenant("x", slug));
    }

    [Fact]
    public void A_blank_name_is_refused() =>
        Should.Throw<ArgumentException>(() => new Tenant("   ", "valid-slug"));
}

public sealed class BrandTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void A_brand_defaults_to_english_when_no_language_is_given()
    {
        var brand = new Brand(Tenant, "Appointso", "Europe/Zagreb");

        brand.Languages.ShouldBe(["en"]);
        brand.PrimaryLanguage.ShouldBe("en");
    }

    [Fact]
    public void Languages_are_normalised_and_deduplicated()
    {
        var brand = new Brand(Tenant, "Appointso", "Europe/Zagreb", [" HR ", "hr", "EN"]);

        brand.Languages.ShouldBe(["hr", "en"]);
        brand.PrimaryLanguage.ShouldBe("hr");
    }

    [Fact]
    public void A_brand_must_belong_to_a_tenant() =>
        Should.Throw<ArgumentException>(() => new Brand(Guid.Empty, "Appointso", "UTC"));

    [Fact]
    public void A_brand_cannot_be_left_without_any_language()
    {
        var brand = new Brand(Tenant, "Appointso", "UTC");

        Should.Throw<ArgumentException>(() => brand.SetLanguages([]));
    }

    [Fact]
    public void An_empty_website_is_stored_as_null()
    {
        var brand = new Brand(Tenant, "Appointso", "UTC", website: "   ");

        brand.Website.ShouldBeNull();
    }
}

public sealed class NewIdTests
{
    [Fact]
    public void Identifiers_are_time_ordered()
    {
        var earlier = NewId.CreateAt(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = NewId.CreateAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // Time ordering is why these index like a sequence instead of fragmenting the
        // B-tree the way a random GUID does.
        string.CompareOrdinal(earlier.ToString(), later.ToString()).ShouldBeLessThan(0);
    }

    [Fact]
    public void Identifiers_are_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => NewId.Create()).ToHashSet();

        ids.Count.ShouldBe(1000);
    }

    [Fact]
    public void Identifiers_are_version_7()
    {
        var id = NewId.Create();

        id.Version.ShouldBe(7);
    }
}
