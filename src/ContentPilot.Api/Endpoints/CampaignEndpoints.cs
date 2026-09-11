using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Campaigns;
using ContentPilot.Domain.Content;
using ContentPilot.Infrastructure.Campaigns;
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
