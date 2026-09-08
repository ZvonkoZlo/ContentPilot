using ContentPilot.Application.Abstractions;
using Shouldly;

namespace ContentPilot.UnitTests.Abstractions;

public sealed class TenantContextTests
{
    [Fact]
    public void No_tenant_is_in_scope_by_default()
    {
        var context = new TenantContext();

        context.TenantId.ShouldBeNull();
        context.IsCrossTenantScope.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => context.RequireTenantId());
    }

    [Fact]
    public void An_empty_tenant_identifier_is_refused() =>
        Should.Throw<ArgumentException>(() => new TenantContext().SetTenant(Guid.Empty));

    [Fact]
    public void A_cross_tenant_scope_restores_the_previous_state_when_disposed()
    {
        var tenant = Guid.NewGuid();
        var context = new TenantContext();
        context.SetTenant(tenant);

        using (context.BeginCrossTenantScope())
        {
            context.IsCrossTenantScope.ShouldBeTrue();
        }

        // A maintenance operation must not leave an unfiltered context behind for
        // whatever runs next in the same scope.
        context.IsCrossTenantScope.ShouldBeFalse();
        context.TenantId.ShouldBe(tenant);
    }

    [Fact]
    public void Nested_cross_tenant_scopes_unwind_correctly()
    {
        var context = new TenantContext();
        context.SetTenant(Guid.NewGuid());

        using (context.BeginCrossTenantScope())
        {
            using (context.BeginCrossTenantScope())
            {
                context.IsCrossTenantScope.ShouldBeTrue();
            }

            context.IsCrossTenantScope.ShouldBeTrue();
        }

        context.IsCrossTenantScope.ShouldBeFalse();
    }

    [Fact]
    public void Disposing_a_scope_twice_is_harmless()
    {
        var context = new TenantContext();
        context.SetTenant(Guid.NewGuid());

        var scope = context.BeginCrossTenantScope();
        scope.Dispose();
        scope.Dispose();

        context.IsCrossTenantScope.ShouldBeFalse();
    }
}
