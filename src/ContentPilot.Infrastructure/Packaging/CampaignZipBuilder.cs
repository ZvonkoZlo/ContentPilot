using System.IO.Compression;
using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Packaging;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Packaging;

namespace ContentPilot.Infrastructure.Packaging;

/// <summary>
/// §12/Phase 8's ZIP, built on demand rather than eagerly at packaging time — most campaigns
/// are browsed and rated file-by-file, and building a ZIP nobody downloads is wasted work.
/// Cached by <see cref="CampaignPackage.ZipKey"/>: <see cref="CampaignPackage.Rebuild"/>
/// clears it whenever the manifest changes, so a stale ZIP is never served, and an unchanged
/// package never pays to rebuild one that already exists.
/// </summary>
public sealed class CampaignZipBuilder(IObjectStore store)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<ObjectKey> BuildOrGetAsync(ContentCampaign campaign, CampaignPackage package, CancellationToken ct)
    {
        if (package.ZipKey is { } existing)
        {
            var existingKey = ObjectKey.FromExisting(existing);

            if (await store.ExistsAsync(existingKey, ct))
            {
                return existingKey;
            }
        }

        var manifest = JsonSerializer.Deserialize<PackageManifest>(package.ManifestJson, Json)
            ?? throw new InvalidOperationException($"Campaign '{campaign.Id}' has an unreadable package manifest.");

        var zipKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/package.zip");

        // A temp file, not a MemoryStream: "streams without buffering a large campaign"
        // means the whole ZIP is never held in RAM at once, only the one entry's bytes
        // being copied at a time.
        var tempPath = Path.GetTempFileName();

        try
        {
            await using (var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(tempFile, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // plan.json and manifest.json describe themselves nowhere in the
                    // manifest's own file list (see CampaignPackager) but belong in the
                    // download all the same.
                    var paths = new[] { "plan.json", "manifest.json" }.Concat(manifest.Files.Select(f => f.Path));

                    foreach (var path in paths)
                    {
                        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();

                        var sourceKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/{path}");
                        await using var source = await store.GetAsync(sourceKey, ct);
                        await source.CopyToAsync(entryStream, ct);
                    }
                }

                tempFile.Position = 0;
                await store.PutAsync(zipKey, tempFile, "application/zip", ct);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }

        package.SetZipKey(zipKey.Value);

        return zipKey;
    }
}
