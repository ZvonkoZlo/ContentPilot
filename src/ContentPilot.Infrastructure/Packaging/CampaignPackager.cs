using System.Text;
using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Packaging;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Packaging;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Packaging;

/// <summary>
/// Phase 8's packaging step: lays a campaign out under <c>campaigns/{campaignId}/...</c> as
/// §12/§13 describe (<c>plan.json</c>, <c>manifest.json</c>, one folder per item), and
/// upserts the <see cref="CampaignPackage"/> row that indexes it. Idempotent and safe to
/// call again — approving an item after the fact, or re-running a completed campaign's
/// packaging step, rebuilds the same layout rather than accumulating stale files.
/// <para>
/// Only <see cref="ContentAssetKind.Image"/> is handled today, because only
/// <see cref="ContentItemType.StaticPost"/> is actually driven end to end (Phase 5's own
/// scope note). Carousel and reel items are still listed in <c>plan.json</c>, honestly
/// marked with no folder, rather than silently skipped.
/// </para>
/// </summary>
public sealed class CampaignPackager(AppDbContext db, IObjectStore store, IClock clock)
{
    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public async Task<CampaignPackage> BuildAsync(ContentCampaign campaign, CancellationToken ct)
    {
        var items = await db.ContentItems
            .Where(i => i.CampaignId == campaign.Id)
            .OrderBy(i => i.Ordinal)
            .ToListAsync(ct);

        var files = new List<PackageManifestFile>();
        var planItems = new List<CampaignPlanItem>();
        var typeCounters = new Dictionary<ContentItemType, int>();

        foreach (var item in items)
        {
            var folder = await PackageItemAsync(campaign, item, typeCounters, files, ct);

            planItems.Add(new CampaignPlanItem
            {
                Ordinal = item.Ordinal,
                Type = item.Type.ToString(),
                Topic = item.Topic,
                PublishDay = item.PublishDay.ToString(),
                Status = item.Status.ToString(),
                Folder = folder,
            });
        }

        var now = clock.UtcNow;

        var plan = new CampaignPlan
        {
            CampaignId = campaign.Id,
            WeekStart = campaign.WeekStart,
            Theme = campaign.Theme,
            BuiltAt = now,
            Items = planItems,
        };

        await UploadJsonAsync(campaign, "plan.json", plan, ct);

        var manifest = new PackageManifest { CampaignId = campaign.Id, BuiltAt = now, Files = files };
        await UploadJsonAsync(campaign, "manifest.json", manifest, ct);

        var manifestJson = JsonSerializer.Serialize(manifest, OutputJson);

        var package = await db.CampaignPackages.FirstOrDefaultAsync(p => p.CampaignId == campaign.Id, ct);

        if (package is null)
        {
            package = new CampaignPackage(campaign.TenantId, campaign.Id, manifestJson, now);
            db.CampaignPackages.Add(package);
        }
        else
        {
            package.Rebuild(manifestJson, now);
        }

        return package;
    }

    /// <returns>The item's folder name, or null when it has nothing packageable yet.</returns>
    private async Task<string?> PackageItemAsync(
        ContentCampaign campaign,
        ContentItem item,
        Dictionary<ContentItemType, int> typeCounters,
        List<PackageManifestFile> files,
        CancellationToken ct)
    {
        var asset = await ResolveAssetAsync(item, ct);

        if (asset is null)
        {
            return null;
        }

        var ordinal = typeCounters.GetValueOrDefault(item.Type) + 1;
        typeCounters[item.Type] = ordinal;
        var folder = $"{FolderPrefix(item.Type)}-{ordinal:D2}";

        // Buffered rather than streamed straight through: the object store's read side may
        // hand back a stream with no known length (a network response stream), and the S3
        // client needs to know the content length up front to sign the PUT.
        var imageBytes = await ReadAllBytesAsync(ObjectKey.FromExisting(asset.StorageKey), ct);
        var imagePath = $"{folder}/image{ExtensionFor(asset.MediaType)}";
        await UploadAsync(campaign, imagePath, new MemoryStream(imageBytes), asset.MediaType, ct);
        files.Add(new PackageManifestFile
        {
            Path = imagePath, Sha256 = asset.Sha256, Bytes = asset.Bytes, ContentType = asset.MediaType,
        });

        var copySet = await LoadCopySetAsync(item, asset.Attempt, ct);

        if (copySet is not null)
        {
            var caption = string.Join("\n\n", copySet.Slots.Select(s => s.Text));
            var captionBytes = Encoding.UTF8.GetBytes(caption);
            var captionPath = $"{folder}/caption.txt";
            await UploadAsync(campaign, captionPath, new MemoryStream(captionBytes), "text/plain; charset=utf-8", ct);
            files.Add(new PackageManifestFile
            {
                Path = captionPath,
                Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(captionBytes)),
                Bytes = captionBytes.Length,
                ContentType = "text/plain; charset=utf-8",
            });
        }

        var metadata = new
        {
            item_id = item.Id,
            type = item.Type.ToString(),
            topic = item.Topic,
            pillar = item.Pillar,
            objective = item.Objective,
            publish_day = item.PublishDay.ToString(),
            attempt = asset.Attempt,
            fact_citations = copySet?.FactCitations ?? [],
        };

        await UploadJsonAsync(campaign, $"{folder}/metadata.json", metadata, ct, files);

        return folder;
    }

    /// <summary>
    /// The winning attempt: the one <c>BestAssetId</c> names when the item was routed to
    /// human review (§8's promotion), otherwise the latest attempt that actually rendered —
    /// true for a normally approved item, whose last attempt is definitionally the good one.
    /// </summary>
    private async Task<ContentAsset?> ResolveAssetAsync(ContentItem item, CancellationToken ct)
    {
        if (item.BestAssetId is { } bestId)
        {
            return await db.ContentAssets.FirstOrDefaultAsync(a => a.Id == bestId, ct);
        }

        return await db.ContentAssets
            .Where(a => a.ContentItemId == item.Id && a.Kind == ContentAssetKind.Image)
            .OrderByDescending(a => a.Attempt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<CopySet?> LoadCopySetAsync(ContentItem item, int attempt, CancellationToken ct)
    {
        var run = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(
            r => r.Scope == WorkflowScope.Item && r.EntityId == item.Id, ct);

        if (run is null)
        {
            return null;
        }

        var step = await db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(
            s => s.WorkflowRunId == run.Id && s.StepName == nameof(ContentItemStatus.Writing) &&
                 s.Attempt == attempt && s.Outcome == WorkflowStepOutcome.Succeeded, ct);

        return step?.ResultJson is null ? null : JsonSerializer.Deserialize<CopySet>(step.ResultJson, ResultJson);
    }

    private async Task UploadJsonAsync<T>(
        ContentCampaign campaign, string relativePath, T value, CancellationToken ct, List<PackageManifestFile>? files = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, OutputJson);
        await UploadAsync(campaign, relativePath, new MemoryStream(json), "application/json", ct);

        files?.Add(new PackageManifestFile
        {
            Path = relativePath,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json)),
            Bytes = json.Length,
            ContentType = "application/json",
        });
    }

    private async Task<byte[]> ReadAllBytesAsync(ObjectKey key, CancellationToken ct)
    {
        await using var source = await store.GetAsync(key, ct);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private async Task UploadAsync(ContentCampaign campaign, string relativePath, Stream content, string contentType, CancellationToken ct)
    {
        using (content)
        {
            var key = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/{relativePath}");
            await store.PutAsync(key, content, contentType, ct);
        }
    }

    private static string FolderPrefix(ContentItemType type) => type switch
    {
        ContentItemType.Reel => "reel",
        _ => "post",
    };

    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => "",
    };
}
