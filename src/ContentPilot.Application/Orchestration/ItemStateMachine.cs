using ContentPilot.Domain.Content;

namespace ContentPilot.Application.Orchestration;

/// <summary>
/// The legality of every transition in §7's item-level state diagram, as a pure lookup. This
/// is the one place that knows the shape of the pipeline; <c>ContentItem.MoveTo</c> only
/// refuses to leave a terminal state, because the entity should not have to know the whole
/// diagram to guard its own field. The orchestrator asks here first — an illegal transition
/// is a bug in the orchestrator, not a runtime condition, so it throws rather than being
/// silently absorbed by either side.
/// </summary>
public static class ItemStateMachine
{
    /// <summary>
    /// The sequential happy path, in order. <see cref="NextStep"/> and the forward legs of
    /// <see cref="IsLegalTransition"/> are both derived from this single list, so the two
    /// can never drift apart.
    /// </summary>
    private static readonly List<ContentItemStatus> HappyPath =
    [
        ContentItemStatus.Pending,
        ContentItemStatus.Directing,
        ContentItemStatus.Writing,
        ContentItemStatus.SpecAssembly,
        ContentItemStatus.AssetGeneration,
        ContentItemStatus.Rendering,
        ContentItemStatus.Validating,
        ContentItemStatus.Approved,
    ];

    /// <summary>States a run may restart the item at. Approved is a destination, never a restart target.</summary>
    public static readonly IReadOnlyList<ContentItemStatus> RestartableSteps =
        [.. HappyPath.Where(s => s != ContentItemStatus.Approved)];

    private static readonly List<ContentItemStatus> RestartableStepsList = [.. RestartableSteps];

    /// <summary>The step immediately after <paramref name="current"/> on the happy path.</summary>
    public static ContentItemStatus NextStep(ContentItemStatus current)
    {
        var index = HappyPath.IndexOf(current);

        if (index < 0 || index == HappyPath.Count - 1)
        {
            throw new InvalidOperationException($"{current} has no next step on the happy path.");
        }

        return HappyPath[index + 1];
    }

    /// <summary>True when <paramref name="candidate"/> is at or before <paramref name="step"/> on the happy path.</summary>
    public static bool IsAtOrBefore(ContentItemStatus candidate, ContentItemStatus step) =>
        HappyPath.IndexOf(candidate) <= HappyPath.IndexOf(step);


    public static bool IsLegalTransition(ContentItemStatus from, ContentItemStatus to)
    {
        if (from == to)
        {
            return false;
        }

        var fromIndex = HappyPath.IndexOf(from);

        // Any non-terminal step can flag repetition risk, get remediated, or wash out
        // through the two review sinks.
        if (fromIndex >= 0 && from != ContentItemStatus.Approved)
        {
            if (to is ContentItemStatus.Remediating or ContentItemStatus.NeedsHumanReview or ContentItemStatus.Failed)
            {
                return true;
            }
        }

        return from switch
        {
            // The forward march. Each step advances exactly one place — skipping a step
            // would mean a spec never got assembled, or a render never got validated.
            ContentItemStatus.Pending => to == ContentItemStatus.Directing,
            ContentItemStatus.Directing => to == ContentItemStatus.Writing,
            ContentItemStatus.Writing => to == ContentItemStatus.SpecAssembly,
            ContentItemStatus.SpecAssembly => to == ContentItemStatus.AssetGeneration,
            ContentItemStatus.AssetGeneration => to == ContentItemStatus.Rendering,
            ContentItemStatus.Rendering => to == ContentItemStatus.Validating,
            ContentItemStatus.Validating => to == ContentItemStatus.Approved,

            // Remediation always lands back on the happy path — never on itself, never
            // forward past where the finding was raised. That check is
            // RemediationRouter's job, not this one's: this switch only says a restart to
            // any earlier or equal step is a shape the diagram allows.
            ContentItemStatus.Remediating => RestartableStepsList.Contains(to),

            _ => false,
        };
    }

    /// <summary>
    /// A remediation target must sit at or before the step whose finding it is answering —
    /// restarting later than the step that produced the finding would be a cycle with no
    /// state change, exactly the loop-safety rule in §8.
    /// </summary>
    public static bool IsValidRemediationTarget(ContentItemStatus sourceStep, ContentItemStatus target) =>
        RestartableStepsList.Contains(target) && IsAtOrBefore(target, sourceStep);
}
