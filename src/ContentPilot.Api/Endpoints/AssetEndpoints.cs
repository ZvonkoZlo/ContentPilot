using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// The asset library. Uploads go through the API rather than a presigned PUT so that every
/// byte is validated and re-encoded before it exists anywhere — a presigned upload would put
/// an unexamined file in the bucket and only inspect it afterwards.
/// </summary>
public static class AssetEndpoints
{
    /// <summary>Generous enough for a retina screenshot, far below what would exhaust memory.</summary>
    private const long MaxUploadBytes = 25 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brands/{brandId:guid}/assets").WithTags("Assets");

        group.MapGet("/", async (
            Guid brandId, AssetKind? kind, bool? includeArchived, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.BrandAssets.AsNoTracking()
                // Derived variants are an implementation detail of the library, not entries in it.
                .Where(a => a.BrandId == brandId && a.ParentAssetId == null);

            if (kind is not null)
            {
                query = query.Where(a => a.Kind == kind);
            }

            if (includeArchived != true)
            {
                query = query.Where(a => !a.IsArchived);
            }

            var assets = await query.OrderBy(a => a.Kind).ThenBy(a => a.FileName).ToListAsync(ct);

            return Results.Ok(assets.Select(AssetResponse.From));
        })
        .WithSummary("Lists library assets.");

        group.MapPost("/", async (
            Guid brandId,
            HttpRequest request,
            AssetLibrary library,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { detail = "Upload the file as multipart/form-data." });
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { detail = "No file was supplied under the 'file' field." });
            }

            if (file.Length > MaxUploadBytes)
            {
                return Results.BadRequest(new { detail = $"The file exceeds the {MaxUploadBytes / 1024 / 1024} MB limit." });
            }

            if (!Enum.TryParse<AssetKind>(form["kind"], ignoreCase: true, out var kind))
            {
                return Results.BadRequest(new
                {
                    detail = $"'kind' must be one of: {string.Join(", ", Enum.GetNames<AssetKind>())}.",
                });
            }

            var tags = form["tags"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            try
            {
                await using var content = file.OpenReadStream();

                var asset = await library.AddAsync(
                    brandId, kind, content, file.FileName, form["description"], tags, ct);

                return Results.Created($"/api/brands/{brandId}/assets/{asset.Id}", AssetResponse.From(asset));
            }
            catch (UnsupportedAssetException ex)
            {
                // Always the uploader's problem, and the message says what to do about it.
                return Results.BadRequest(new { detail = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { detail = ex.Message });
            }
        })
        .WithSummary("Uploads an asset. Validated by signature, re-encoded, and stripped of metadata.")
        .DisableAntiforgery();

        group.MapGet("/{assetId:guid}/url", async (
            Guid brandId, Guid assetId, AssetLibrary library, CancellationToken ct) =>
        {
            try
            {
                var url = await library.GetDownloadUrlAsync(assetId, TimeSpan.FromMinutes(15), ct);

                return Results.Ok(new { url });
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        })
        .WithSummary("Mints a 15-minute presigned URL for one asset.");

        group.MapPost("/{assetId:guid}/archive", async (
            Guid brandId, Guid assetId, AppDbContext db, CancellationToken ct) =>
        {
            var asset = await db.BrandAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.BrandId == brandId, ct);

            if (asset is null)
            {
                return Results.NotFound();
            }

            // Archived, never deleted: a campaign already produced from this asset must stay
            // explainable, and that means the row it points at has to survive.
            asset.Archive();
            await db.SaveChangesAsync(ct);

            return Results.Ok(AssetResponse.From(asset));
        })
        .WithSummary("Archives an asset so agents stop selecting it.");

        return app;
    }
}

public sealed record AssetResponse(
    Guid Id, string Kind, string FileName, string MediaType, int Width, int Height,
    long Bytes, string Origin, IReadOnlyList<string> Tags, IReadOnlyList<string> DominantColors,
    string? Description, bool IsArchived, DateTimeOffset CreatedAt)
{
    public static AssetResponse From(BrandAsset a) =>
        new(a.Id, a.Kind.ToString(), a.FileName, a.MediaType, a.Width, a.Height, a.Bytes,
            a.Origin.ToString(), a.Tags, a.DominantColors, a.Description, a.IsArchived, a.CreatedAt);
}
