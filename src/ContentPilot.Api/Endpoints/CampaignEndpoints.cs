using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Campaigns;
using ContentPilot.Application.Quality;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Packaging;
using ContentPilot.Infrastructure.Campaigns;
using ContentPilot.Infrastructure.Packaging;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// The manual trigger from §21: "generate now" for one brand's week, and the read side an
/// operator or a review UI needs to watch it work. The weekly schedule and the daily
/// reconciler that catches a missed one call the exact same <see cref="CampaignStarter"/>
/// this endpoint does — a scheduled trigger and a manual one are never two code paths.
/// </summary>
public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns").WithTags("Campaigns");

        group.MapPost("/", async (
            GenerateCampaignRequest request,
            AppDbContext db,
            CampaignStarter starter,
            IUnitOfWork uow,
            IClock clock,
            CancellationToken ct) =>
        {
            var brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BrandId, ct);

            if (brand is null)
            {
                return Results.NotFound(new { detail = $"No brand '{request.BrandId}'." });
            }

            var weekStart = request.WeekStart ?? CampaignWeek.MondayOnOrAfter(DateOnly.FromDateTime(clock.UtcNow.Date));

            var campaign = await starter.StartAsync(brand, weekStart, request.Trigger ?? CampaignTrigger.Manual, ct);

            if (campaign is null)
            {
                return Results.Conflict(new { detail = $"A campaign for the week of {weekStart:yyyy-MM-dd} already exists." });
            }

            // Enqueue already joined this same transaction inside StartAsync: the job
            // cannot exist for a campaign row that then fails to commit, and the row
            // cannot commit without its job queued.
            await uow.SaveChangesAsync(ct);

            return Results.Accepted($"/api/campaigns/{campaign.Id}", ToResponse(campaign));
        })
        .WithSummary("Triggers a campaign for one brand's week. \"Generate now\", the weekly schedule and the daily reconciler all call this.");

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var campaign = await db.ContentCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

            return campaign is null ? Results.NotFound() : Results.Ok(ToResponse(campaign));
        })
        .WithSummary("Reads one campaign's status.");

        group.MapGet("/{id:guid}/items", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.ContentItems
                .AsNoTracking()
                .Where(i => i.CampaignId == id)
                .OrderBy(i => i.Ordinal)
                .Select(i => new ContentItemResponse(
                    i.Id, i.Ordinal, i.Type.ToString(), i.Topic, i.Pillar, i.PublishDay.ToString(),
                    i.Status.ToString(), i.QualityAttempts, i.FailureReason))
                .ToListAsync(ct);

            return Results.Ok(items);
        })
        .WithSummary("Lists one campaign's items and their current status — the review UI's main query.");

        group.MapPost("/{id:guid}/cancel", async (Guid id, AppDbContext db, IUnitOfWork uow, IClock clock, CancellationToken ct) =>
        {
            var campaign = await db.ContentCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct);

            if (campaign is null)
            {
                return Results.NotFound();
            }

            if (campaign.IsTerminal)
            {
                // Already finished, already failed, already cancelled — cancelling it again
                // is a no-op, not an error the caller needs to react to.
                return Results.Ok(ToResponse(campaign));
            }

            campaign.Cancel(clock.UtcNow);
            await uow.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(campaign));
        })
        .WithSummary(
            "Cancels a campaign that has not finished. CampaignWorkflowJobHandler stops " +
            "acting on it the next time it is dispatched; items already in flight keep " +
            "running to their own terminal state — cancellation does not reach into them yet.");

        group.MapGet("/{id:guid}/package", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var package = await db.CampaignPackages.AsNoTracking().FirstOrDefaultAsync(p => p.CampaignId == id, ct);

            return package is null
                ? Results.NotFound(new { detail = "This campaign has not been packaged yet." })
                : Results.Ok(new CampaignPackageResponse(package.CampaignId, package.BuiltAt, package.EmailSentAt, package.ManifestJson));
        })
        .WithSummary(
            "Reads a campaign's built package: the manifest.json content plus when it was " +
            "built. 404 until the campaign reaches Packaging. The files themselves are read " +
            "through object storage directly — this endpoint is the index, not the download.");

        group.MapPost("/{id:guid}/download", async (
            Guid id, AppDbContext db, IObjectStore store, CampaignZipBuilder zipBuilder, IUnitOfWork uow, CancellationToken ct) =>
        {
            var campaign = await db.ContentCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct);

            if (campaign is null)
            {
                return Results.NotFound();
            }

            var package = await db.CampaignPackages.FirstOrDefaultAsync(p => p.CampaignId == id, ct);

            if (package is null)
            {
                return Results.NotFound(new { detail = "This campaign has not been packaged yet." });
            }

            var zipKey = await zipBuilder.BuildOrGetAsync(campaign, package, ct);
            await uow.SaveChangesAsync(ct); // persists ZipKey if the ZIP was just built

            var url = await store.GetPresignedReadUrlAsync(zipKey, TimeSpan.FromMinutes(15), ct);

            return Results.Ok(new { url });
        })
        .WithSummary(
            "Builds (or reuses a cached) ZIP of the whole campaign and returns a short-lived " +
            "presigned download URL. The ZIP is rebuilt automatically the next time this is " +
            "called after the package itself changes (an item approved late, say).");

        group.MapGet("/{id:guid}/qa-pass-rate", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var exists = await db.ContentCampaigns.AsNoTracking().AnyAsync(c => c.Id == id, ct);

            if (!exists)
            {
                return Results.NotFound();
            }

            var items = await db.ContentItems
                .AsNoTracking()
                .Where(i => i.CampaignId == id)
                .Select(i => new QaPassRateItem { Status = i.Status, QualityAttempts = i.QualityAttempts })
                .ToListAsync(ct);

            return Results.Ok(QaPassRateCalculator.Calculate(items));
        })
        .WithSummary(
            "§9's headline QA metric for one campaign: what fraction of terminal items were " +
            "approved on their first quality attempt, with no remediation restart. Null " +
            "until at least one item reaches a terminal state.");

        group.MapPost("/{id:guid}/items/{itemId:guid}/rating", async (
            Guid id, Guid itemId, RateItemRequest request, AppDbContext db, IUnitOfWork uow, IClock clock, CancellationToken ct) =>
        {
            var item = await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.CampaignId == id, ct);

            if (item is null)
            {
                return Results.NotFound(new { detail = $"No item '{itemId}' in campaign '{id}'." });
            }

            var now = clock.UtcNow;
            var rating = await db.HumanRatings.FirstOrDefaultAsync(r => r.ContentItemId == itemId, ct);

            if (rating is null)
            {
                rating = new HumanRating(item.TenantId, itemId, request.Score, now, request.Note);
                db.HumanRatings.Add(rating);
            }
            else
            {
                rating.Update(request.Score, now, request.Note);
            }

            await uow.SaveChangesAsync(ct);

            return Results.Ok(new RatingResponse(rating.ContentItemId, rating.Score, rating.Note, rating.RatedAt));
        })
        .WithSummary("Records the review UI's 1–5 \"would I publish this\" rating for one item. A second call replaces the first.");

        return app;
    }

    private static CampaignResponse ToResponse(ContentCampaign campaign) => new(
        campaign.Id, campaign.BrandId, campaign.WeekStart, campaign.Trigger.ToString(),
        campaign.Status.ToString(), campaign.Theme, campaign.FailureReason, campaign.CompletedAt);
}

public sealed record GenerateCampaignRequest(Guid BrandId, DateOnly? WeekStart, CampaignTrigger? Trigger);

public sealed record CampaignResponse(
    Guid Id,
    Guid BrandId,
    DateOnly WeekStart,
    string Trigger,
    string Status,
    string? Theme,
    string? FailureReason,
    DateTimeOffset? CompletedAt);

public sealed record ContentItemResponse(
    Guid Id,
    int Ordinal,
    string Type,
    string Topic,
    string Pillar,
    string PublishDay,
    string Status,
    int QualityAttempts,
    string? FailureReason);

public sealed record CampaignPackageResponse(Guid CampaignId, DateTimeOffset BuiltAt, DateTimeOffset? EmailSentAt, string ManifestJson);

public sealed record RateItemRequest(int Score, string? Note);

public sealed record RatingResponse(Guid ContentItemId, int Score, string? Note, DateTimeOffset RatedAt);
