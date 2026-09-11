using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// The manual trigger from §21: "generate now" for one brand's week, and the read side an
/// operator or a review UI needs to watch it work. The weekly Hangfire cron this system
/// will eventually also have enqueues the exact same job this endpoint does — a scheduled
/// trigger and a manual one are never two code paths.
/// </summary>
public static class CampaignEndpoints
{
    public static IEndpointRouteBuilder MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns").WithTags("Campaigns");

        group.MapPost("/", async (
            GenerateCampaignRequest request,
            AppDbContext db,
            IBrandBrainReader brandReader,
            IJobQueue jobs,
            IUnitOfWork uow,
            ITenantContext tenantContext,
            IClock clock,
            CancellationToken ct) =>
        {
            var brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BrandId, ct);

            if (brand is null)
            {
                return Results.NotFound(new { detail = $"No brand '{request.BrandId}'." });
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantContext.RequireTenantId(), ct)
                ?? throw new InvalidOperationException("The tenant in scope has no row. This is a data integrity bug, not a request error.");

            var weekStart = request.WeekStart ?? MondayOnOrAfter(DateOnly.FromDateTime(clock.UtcNow.Date));

            // One campaign per brand per week is a database constraint, not just a
            // convention — a double-fired trigger for the same week is caught there even
            // if this check somehow races it.
            var alreadyExists = await db.ContentCampaigns
                .AnyAsync(c => c.BrandId == request.BrandId && c.WeekStart == weekStart, ct);

            if (alreadyExists)
            {
                return Results.Conflict(new { detail = $"A campaign for the week of {weekStart:yyyy-MM-dd} already exists." });
            }

            // Frozen now, at trigger time — every agent this campaign runs works from this
            // exact snapshot, so an edit to the live profile mid-week cannot change what is
            // already in flight.
            var version = await brandReader.CaptureVersionAsync(request.BrandId, ct);

            var campaign = new ContentCampaign(
                tenant.Id, request.BrandId, weekStart,
                request.Trigger ?? CampaignTrigger.Manual,
                version.VersionId, tenant.Limits.MaxCostPerCampaignMicroCents);

            db.ContentCampaigns.Add(campaign);

            // Enqueue joins this same transaction: the job cannot exist for a campaign row
            // that then fails to commit, and the row cannot commit without its job queued.
            await jobs.EnqueueAsync(
                new AdvanceCampaignWorkflowPayload(campaign.Id), tenant.Id,
                idempotencyKey: $"campaign-plan:{campaign.Id:N}", ct: ct);

            await uow.SaveChangesAsync(ct);

            return Results.Accepted($"/api/campaigns/{campaign.Id}", ToResponse(campaign));
        })
        .WithSummary("Triggers a campaign for one brand's week. \"Generate now\" and the weekly cron both call this.");

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

        return app;
    }

    private static DateOnly MondayOnOrAfter(DateOnly date)
    {
        var offset = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;

        return date.AddDays(offset);
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
