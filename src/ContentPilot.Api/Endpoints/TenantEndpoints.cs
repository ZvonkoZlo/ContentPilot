using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// Tenant administration. Deliberately outside tenant resolution — this is where a
/// tenant comes into existence, so it cannot require one to already be in scope.
/// </summary>
public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenants").WithTags("Tenants");

        group.MapGet("/", async (AppDbContext db, IMutableTenantContext tenantContext, CancellationToken ct) =>
        {
            using var _ = tenantContext.BeginCrossTenantScope();

            var tenants = await db.Tenants
                .AsNoTracking()
                .OrderBy(t => t.Name)
                .Select(t => new TenantResponse(t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(tenants);
        })
        .WithSummary("Lists tenants.");

        group.MapPost("/", async (
            CreateTenantRequest request,
            AppDbContext db,
            IMutableTenantContext tenantContext,
            CancellationToken ct) =>
        {
            using var _ = tenantContext.BeginCrossTenantScope();

            if (await db.Tenants.AnyAsync(t => t.Slug == request.Slug.ToLower(), ct))
            {
                return Results.Conflict(new { detail = $"A tenant with slug '{request.Slug}' already exists." });
            }

            Tenant tenant;

            try
            {
                tenant = new Tenant(request.Name, request.Slug);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = ex.Message });
            }

            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);

            var response = new TenantResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.IsActive, tenant.CreatedAt);

            return Results.Created($"/api/tenants/{tenant.Id}", response);
        })
        .WithSummary("Creates a tenant.");

        return app;
    }
}

public sealed record CreateTenantRequest(string Name, string Slug);

public sealed record TenantResponse(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt);
