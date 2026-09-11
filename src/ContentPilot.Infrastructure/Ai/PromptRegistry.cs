using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Observability;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Gives every prompt a durable identity, so an output produced months ago can still be
/// traced to the exact text behind it.
/// <para>
/// The guard that matters is the hash check. Editing a prompt without bumping its version
/// would silently rewrite the history of every run that already pointed at it — the audit
/// trail would still look intact while no longer being true. That is refused.
/// </para>
/// </summary>
public sealed class PromptRegistry(
    AppDbContext db,
    IMutableTenantContext tenantContext,
    IClock clock,
    ILogger<PromptRegistry> logger)
{
    private readonly Dictionary<string, Guid> _cache = new(StringComparer.Ordinal);

    public async Task<Guid> ResolveAsync(PromptTemplate prompt, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(prompt.Reference, out var cached))
        {
            return cached;
        }

        // Prompts are platform assets shared across tenants, so this read and write happen
        // outside tenant scope on purpose.
        using var _ = tenantContext.BeginCrossTenantScope();

        var existing = await db.PromptVersions
            .FirstOrDefaultAsync(v => v.PromptId == prompt.PromptId && v.Version == prompt.Version, ct);

        if (existing is not null)
        {
            if (existing.ContentHash != prompt.ContentHash)
            {
                throw new InvalidOperationException(
                    $"{prompt.Reference} has been edited without bumping its version. " +
                    $"Registered hash {existing.ContentHash[..12]}, current {prompt.ContentHash[..12]}. " +
                    "Every run recorded against this version would silently start meaning " +
                    "something else. Bump the version instead.");
            }

            _cache[prompt.Reference] = existing.Id;

            return existing.Id;
        }

        var version = new PromptVersion(
            prompt.PromptId, prompt.Version, $"{prompt.System}\n---\n{prompt.User}",
            prompt.ContentHash, prompt.ModelProfile, prompt.SchemaName, clock.UtcNow);

        db.PromptVersions.Add(version);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Registered prompt {Reference} ({Hash}).", prompt.Reference, prompt.ContentHash[..12]);

        _cache[prompt.Reference] = version.Id;

        return version.Id;
    }

    /// <summary>
    /// Registers every embedded prompt at startup, so an edited prompt fails the process
    /// rather than the first campaign that happens to use it.
    /// </summary>
    public async Task RegisterAllAsync(PromptLibrary library, CancellationToken ct = default)
    {
        foreach (var prompt in library.All)
        {
            await ResolveAsync(prompt, ct);
        }
    }
}
