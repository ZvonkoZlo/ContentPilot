using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Tenancy;

/// <summary>
/// Resolves the ambient tenant once, at the edge, before any use case runs.
/// <para>
/// MVP source is the <c>X-Tenant-Id</c> header, because the MVP has a single operator and
/// no public signup. When authentication lands, the claim replaces the header here and
/// nothing downstream changes — that is the whole point of resolving it in one place.
/// </para>
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Tenant-Id";

    /// <summary>Paths that legitimately run without a tenant.</summary>
    private static readonly string[] AnonymousPrefixes =
    [
        "/health",
        "/openapi",
        "/scalar",
        "/api/tenants",
    ];

    public async Task InvokeAsync(HttpContext context, IMutableTenantContext tenantContext, AppDbContext db)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var raw) || !Guid.TryParse(raw, out var tenantId))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                $"A valid {HeaderName} header is required for this endpoint.");
            return;
        }

        // Verified against the database rather than trusted: an unknown identifier must
        // not open an empty but valid-looking tenant scope.
        bool exists;

        using (tenantContext.BeginCrossTenantScope())
        {
            exists = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.IsActive, context.RequestAborted);
        }

        if (!exists)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Unknown or inactive tenant.");
            return;
        }

        tenantContext.SetTenant(tenantId);

        System.Diagnostics.Activity.Current?.SetTag("tenant.id", tenantId);

        await next(context);
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        return context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = statusCode == StatusCodes.Status404NotFound ? "Not Found" : "Bad Request",
            status = statusCode,
            detail,
        });
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}
