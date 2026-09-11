using ContentPilot.Renderer.Templates.Reel;

namespace ContentPilot.Renderer.Video;

public static class ReelTemplateComponentRegistry
{
    private static readonly IReadOnlyDictionary<string, Type> Components =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["reel-problem-solution"] = typeof(ProblemSolution),
            ["reel-feature-tour"] = typeof(FeatureTour),
            ["reel-before-after"] = typeof(BeforeAfter),
        };

    public static Type Get(string templateId) =>
        Components.TryGetValue(templateId, out var component)
            ? component
            : throw new ReelRenderException($"No reel component implements template '{templateId}'.");
}
