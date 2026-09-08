using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// Brand CRUD. Every query here is silently tenant-filtered by the DbContext, so no
/// endpoint in this file mentions a tenant identifier — which is exactly the property
/// that makes cross-tenant leakage hard to write by accident.
/// </summary>
public static class BrandEndpoints
{
    public static IEndpointRouteBuilder MapBrandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brands").WithTags("Brands");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var brands = await db.Brands
                .AsNoTracking()
                .OrderBy(b => b.Name)
                .ToListAsync(ct);

            return Results.Ok(brands.Select(ToResponse));
        })
        .WithSummary("Lists brands for the tenant in scope.");

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

            // A brand in another tenant is filtered out of the query entirely, so it
            // returns 404 rather than 403 — no existence is disclosed across tenants.
            return brand is null ? Results.NotFound() : Results.Ok(ToResponse(brand));
        })
        .WithSummary("Gets one brand.");

        group.MapPost("/", async (
            CreateBrandRequest request,
            AppDbContext db,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            Brand brand;

            try
            {
                brand = new Brand(
                    tenantContext.RequireTenantId(),
                    request.Name,
                    request.TimeZoneId ?? "UTC",
                    request.Languages,
                    request.Website);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = ex.Message });
            }

            db.Brands.Add(brand);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/brands/{brand.Id}", ToResponse(brand));
        })
        .WithSummary("Creates a brand.");

        return app;
    }

    private static BrandResponse ToResponse(Brand brand) => new(
        brand.Id,
        brand.Name,
        brand.Website,
        brand.TimeZoneId,
        brand.Languages,
        brand.IsActive,
        brand.CreatedAt);
}

public sealed record CreateBrandRequest(
    string Name,
    string? Website,
    string? TimeZoneId,
    IReadOnlyList<string>? Languages);

public sealed record BrandResponse(
    Guid Id,
    string Name,
    string? Website,
    string TimeZoneId,
    IReadOnlyList<string> Languages,
    bool IsActive,
    DateTimeOffset CreatedAt);
