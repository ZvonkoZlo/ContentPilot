using ContentPilot.Application.Ai;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Resolves profile names from configuration, and refuses to start if the set is unusable.
/// A missing profile discovered mid-campaign wastes whatever the run already spent.
/// </summary>
public sealed class ModelProfileRegistry : IModelProfileRegistry
{
    private readonly Dictionary<string, ModelProfile> _profiles;

    public ModelProfileRegistry(IOptions<AiOptions> options)
    {
        var configured = options.Value.Profiles;

        if (configured.Count == 0)
        {
            throw new InvalidOperationException(
                "No model profiles configured. Add an 'Ai:Profiles' section; agents address " +
                "profiles by name and cannot fall back to a default.");
        }

        _profiles = configured.ToDictionary(
            pair => pair.Key,
            pair => new ModelProfile
            {
                Name = pair.Key,
                ModelId = string.IsNullOrWhiteSpace(pair.Value.ModelId)
                    ? throw new InvalidOperationException($"Profile '{pair.Key}' has no ModelId.")
                    : pair.Value.ModelId,
                Effort = pair.Value.Effort,
                MaxOutputTokens = pair.Value.MaxOutputTokens,
                InputPricePerMillion = pair.Value.InputPricePerMillion,
                OutputPricePerMillion = pair.Value.OutputPricePerMillion,
                CacheReadPricePerMillion = pair.Value.CacheReadPricePerMillion,
                CacheWritePricePerMillion = pair.Value.CacheWritePricePerMillion,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ModelProfile> All => _profiles.Values;

    public ModelProfile Get(string name) =>
        _profiles.TryGetValue(name, out var profile)
            ? profile
            : throw new InvalidOperationException(
                $"No model profile '{name}'. Configured: {string.Join(", ", _profiles.Keys.Order())}.");
}
