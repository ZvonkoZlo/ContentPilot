using ContentPilot.Application.Abstractions;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// Advances one item's <c>WorkflowRun</c> by one turn of the core loop. Enqueued once when
/// the item is created, and again by the handler itself after every turn that leaves the
/// run still active — the same "execute, persist, re-enqueue" shape every job in this
/// system already follows.
/// </summary>
[JobType("advance-content-item-workflow")]
public sealed record AdvanceContentItemWorkflowPayload(Guid ContentItemId);
