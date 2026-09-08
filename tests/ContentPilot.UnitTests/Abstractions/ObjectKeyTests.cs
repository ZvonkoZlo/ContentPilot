using ContentPilot.Application.Abstractions;
using Shouldly;

namespace ContentPilot.UnitTests.Abstractions;

/// <summary>
/// A storage key that is tenant-prefixed by construction is the difference between
/// isolation you enforce and isolation you hope for.
/// </summary>
public sealed class ObjectKeyTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Keys_are_always_tenant_prefixed()
    {
        var key = ObjectKey.ForTenant(Tenant, "brand/abc/assets/logo.png");

        key.Value.ShouldBe("t/11111111222233334444555555555555/brand/abc/assets/logo.png");
        key.Value.ShouldStartWith(ObjectKey.TenantPrefix(Tenant));
    }

    [Theory]
    [InlineData("/leading/slash.png")]
    [InlineData("\\windows\\style.png")]
    [InlineData("  spaced.png  ")]
    public void Paths_are_normalised(string input)
    {
        var key = ObjectKey.ForTenant(Tenant, input);

        key.Value.ShouldStartWith(ObjectKey.TenantPrefix(Tenant));
        key.Value.ShouldNotContain("\\");
        key.Value.ShouldNotContain("//");
    }

    [Theory]
    [InlineData("../other-tenant/logo.png")]
    [InlineData("brand/../../escape.png")]
    public void Traversal_out_of_the_tenant_prefix_is_refused(string input) =>
        Should.Throw<ArgumentException>(() => ObjectKey.ForTenant(Tenant, input));

    [Fact]
    public void An_empty_tenant_is_refused() =>
        Should.Throw<ArgumentException>(() => ObjectKey.ForTenant(Guid.Empty, "file.png"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_refused(string input) =>
        Should.Throw<ArgumentException>(() => ObjectKey.ForTenant(Tenant, input));

    [Fact]
    public void Keys_read_back_from_storage_round_trip_unchanged()
    {
        var original = ObjectKey.ForTenant(Tenant, "campaigns/x/plan.json");

        ObjectKey.FromExisting(original.Value).ShouldBe(original);
    }
}
