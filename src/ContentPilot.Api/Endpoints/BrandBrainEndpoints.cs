using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Brand;
using ContentPilot.Application.Capabilities;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// Everything an operator edits about a brand. Tenant filtering is silent here — no handler
/// mentions a tenant, which is what makes cross-tenant leakage hard to write by accident.
/// </summary>
public static class BrandBrainEndpoints
{
    public static IEndpointRouteBuilder MapBrandBrainEndpoints(this IEndpointRouteBuilder app)
    {
        var brand = app.MapGroup("/api/brands/{brandId:guid}").WithTags("Brand Brain");

        brand.MapGet("/profile", async (Guid brandId, AppDbContext db, CancellationToken ct) =>
        {
            var profile = await db.BrandProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.BrandId == brandId, ct);

            return profile is null
                ? Results.NotFound(new { detail = "This brand has no profile yet." })
                : Results.Ok(new ProfileResponse(profile.Visual, profile.Voice, profile.Messaging, profile.OperatorNotes));
        })
        .WithSummary("Reads the brand profile.");

        brand.MapPut("/profile", async (
            Guid brandId, ProfileRequest request, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Brands.AnyAsync(b => b.Id == brandId, ct))
            {
                return Results.NotFound();
            }

            // Colours end up in a stylesheet, so they are validated before they are stored
            // rather than sanitised on the way out.
            var errors = request.Visual.Validate().ToArray();

            if (errors.Length > 0)
            {
                return Results.BadRequest(new { detail = string.Join(" ", errors) });
            }

            var profile = await db.BrandProfiles.FirstOrDefaultAsync(p => p.BrandId == brandId, ct);

            if (profile is null)
            {
                profile = new BrandProfile(tenant.RequireTenantId(), brandId, request.Visual, request.Voice, request.Messaging);
                db.BrandProfiles.Add(profile);
            }
            else
            {
                profile.UpdateVisual(request.Visual);
                profile.UpdateVoice(request.Voice);
                profile.UpdateMessaging(request.Messaging);
            }

            profile.SetOperatorNotes(request.OperatorNotes);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ProfileResponse(profile.Visual, profile.Voice, profile.Messaging, profile.OperatorNotes));
        })
        .WithSummary("Creates or replaces the brand profile.");

        brand.MapGet("/facts", async (Guid brandId, AppDbContext db, CancellationToken ct) =>
        {
            var facts = await db.ProductFacts.AsNoTracking()
                .Where(f => f.BrandId == brandId)
                .OrderBy(f => f.Category).ThenBy(f => f.Key)
                .ToListAsync(ct);

            return Results.Ok(facts.Select(FactResponse.From));
        })
        .WithSummary("Lists product facts. These are what copy must cite.");

        brand.MapPost("/facts", async (
            Guid brandId, FactRequest request, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Brands.AnyAsync(b => b.Id == brandId, ct))
            {
                return Results.NotFound();
            }

            ProductFact fact;

            try
            {
                fact = new ProductFact(tenant.RequireTenantId(), brandId, request.Key, request.Statement, request.Category);
                fact.SetEvidence(request.Evidence);
                fact.SetVisibility(request.IsPublic ?? true);
                fact.SetValidity(request.ValidFrom, request.ValidTo);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = ex.Message });
            }

            if (await db.ProductFacts.AnyAsync(f => f.BrandId == brandId && f.Key == fact.Key, ct))
            {
                return Results.Conflict(new { detail = $"A fact with key '{fact.Key}' already exists for this brand." });
            }

            db.ProductFacts.Add(fact);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/brands/{brandId}/facts/{fact.Id}", FactResponse.From(fact));
        })
        .WithSummary("Adds a citable product fact.");

        brand.MapDelete("/facts/{factId:guid}", async (
            Guid brandId, Guid factId, AppDbContext db, CancellationToken ct) =>
        {
            var fact = await db.ProductFacts.FirstOrDefaultAsync(f => f.Id == factId && f.BrandId == brandId, ct);

            if (fact is null)
            {
                return Results.NotFound();
            }

            db.ProductFacts.Remove(fact);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
        .WithSummary("Removes a product fact.");

        brand.MapGet("/personas", async (Guid brandId, AppDbContext db, CancellationToken ct) =>
        {
            var personas = await db.AudiencePersonas.AsNoTracking()
                .Where(p => p.BrandId == brandId)
                .OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Name)
                .ToListAsync(ct);

            return Results.Ok(personas.Select(p => new PersonaResponse(p.Id, p.Name, p.Segment, p.IsPrimary, p.Detail)));
        })
        .WithSummary("Lists audience personas.");

        brand.MapPost("/personas", async (
            Guid brandId, PersonaRequest request, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Brands.AnyAsync(b => b.Id == brandId, ct))
            {
                return Results.NotFound();
            }

            var persona = new AudiencePersona(tenant.RequireTenantId(), brandId, request.Name, request.Segment, request.Detail);

            if (request.IsPrimary == true)
            {
                // Exactly one primary: the strategist defaults to it, and two would make
                // that default arbitrary.
                await db.AudiencePersonas
                    .Where(p => p.BrandId == brandId && p.IsPrimary)
                    .ForEachAsync(p => p.ClearPrimary(), ct);

                persona.MakePrimary();
            }

            db.AudiencePersonas.Add(persona);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/brands/{brandId}/personas/{persona.Id}",
                new PersonaResponse(persona.Id, persona.Name, persona.Segment, persona.IsPrimary, persona.Detail));
        })
        .WithSummary("Adds an audience persona.");

        brand.MapGet("/preferences", async (Guid brandId, AppDbContext db, CancellationToken ct) =>
        {
            var prefs = await db.ContentPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.BrandId == brandId, ct);

            return prefs is null
                ? Results.NotFound(new { detail = "This brand has no content preferences yet." })
                : Results.Ok(PreferencesResponse.From(prefs));
        })
        .WithSummary("Reads the weekly quota and its boundaries.");

        brand.MapPut("/preferences", async (
            Guid brandId, PreferencesRequest request, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Brands.AnyAsync(b => b.Id == brandId, ct))
            {
                return Results.NotFound();
            }

            var prefs = await db.ContentPreferences.FirstOrDefaultAsync(p => p.BrandId == brandId, ct);

            if (prefs is null)
            {
                prefs = new ContentPreferences(tenant.RequireTenantId(), brandId);
                db.ContentPreferences.Add(prefs);
            }

            try
            {
                prefs.SetQuota(request.PostsPerWeek, request.CarouselsPerWeek, request.ReelsPerWeek);
                prefs.SetExcludedTopics(request.ExcludedTopics ?? []);
                prefs.SetPreferredTopics(request.PreferredTopics ?? []);

                if (request.PublishDays is { Count: > 0 })
                {
                    prefs.SetPublishDays(request.PublishDays);
                }

                prefs.SetSchedule(
                    request.GenerationDay ?? DayOfWeek.Monday,
                    request.GenerationTime ?? new TimeOnly(6, 0),
                    request.ScheduledGenerationEnabled ?? true);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { detail = ex.Message });
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(PreferencesResponse.From(prefs));
        })
        .WithSummary("Sets the weekly quota, excluded topics and generation schedule.");

        brand.MapGet("/snapshot", async (Guid brandId, IBrandBrainReader reader, CancellationToken ct) =>
        {
            try
            {
                var snapshot = await reader.GetSnapshotAsync(brandId, ct);

                return Results.Ok(new SnapshotResponse(
                    snapshot.ComputeHash(),
                    BrandBrainAssembler.Diagnose(snapshot),
                    snapshot));
            }
            catch (BrandNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithSummary("Composes the current Brand Brain, with warnings about what is missing.");

        brand.MapGet("/brand-block", async (
            Guid brandId, IBrandBrainReader reader, CancellationToken ct) =>
        {
            var snapshot = await reader.GetSnapshotAsync(brandId, ct);
            var block = BrandBlockRenderer.Render(snapshot);

            // Exactly what the agents will be given. Worth reading before blaming a model
            // for what it wrote.
            return Results.Ok(new BrandBlockResponse(
                BrandBlockRenderer.EstimateTokens(block), BrandBlockRenderer.DefaultTokenBudget, block));
        })
        .WithSummary("Renders the prompt block the agents will actually see.");

        brand.MapPost("/versions", async (Guid brandId, IBrandBrainReader reader, CancellationToken ct) =>
        {
            var version = await reader.CaptureVersionAsync(brandId, ct);

            return Results.Ok(new
            {
                versionId = version.VersionId,
                contentHash = version.ContentHash,
                created = version.WasCreated,
            });
        })
        .WithSummary("Freezes the Brand Brain as an immutable, hashed version.");

        return app;
    }
}

public sealed record ProfileRequest(
    VisualIdentity Visual, ToneOfVoice Voice, Messaging Messaging, string? OperatorNotes);

public sealed record ProfileResponse(
    VisualIdentity Visual, ToneOfVoice Voice, Messaging Messaging, string? OperatorNotes);

public sealed record FactRequest(
    string Key, string Statement, FactCategory Category, string? Evidence,
    bool? IsPublic, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

public sealed record FactResponse(
    Guid Id, string Key, string Statement, string Category, string? Evidence,
    bool IsPublic, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo)
{
    public static FactResponse From(ProductFact f) =>
        new(f.Id, f.Key, f.Statement, f.Category.ToString(), f.Evidence, f.IsPublic, f.ValidFrom, f.ValidTo);
}

public sealed record PersonaRequest(string Name, string Segment, PersonaDetail Detail, bool? IsPrimary);

public sealed record PersonaResponse(Guid Id, string Name, string Segment, bool IsPrimary, PersonaDetail Detail);

public sealed record PreferencesRequest(
    int PostsPerWeek, int CarouselsPerWeek, int ReelsPerWeek,
    IReadOnlyList<string>? ExcludedTopics, IReadOnlyList<string>? PreferredTopics,
    IReadOnlyList<DayOfWeek>? PublishDays,
    DayOfWeek? GenerationDay, TimeOnly? GenerationTime, bool? ScheduledGenerationEnabled);

public sealed record PreferencesResponse(
    int PostsPerWeek, int CarouselsPerWeek, int ReelsPerWeek, int TotalPerWeek,
    IReadOnlyList<string> ExcludedTopics, IReadOnlyList<string> PreferredTopics,
    IReadOnlyList<DayOfWeek> PublishDays,
    DayOfWeek GenerationDay, TimeOnly GenerationTime, bool ScheduledGenerationEnabled)
{
    public static PreferencesResponse From(ContentPreferences p) =>
        new(p.PostsPerWeek, p.CarouselsPerWeek, p.ReelsPerWeek, p.TotalItemsPerWeek,
            p.ExcludedTopics, p.PreferredTopics, p.PublishDays,
            p.GenerationDay, p.GenerationTime, p.ScheduledGenerationEnabled);
}

public sealed record SnapshotResponse(string ContentHash, IReadOnlyList<string> Warnings, BrandSnapshot Snapshot);

public sealed record BrandBlockResponse(int EstimatedTokens, int TokenBudget, string Block);
