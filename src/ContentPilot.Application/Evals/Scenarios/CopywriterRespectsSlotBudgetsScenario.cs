using ContentPilot.Application.Agents;
using ContentPilot.Domain.Evals;

namespace ContentPilot.Application.Evals.Scenarios;

/// <summary>
/// §27's catalogue: "Copywriter respects slot budgets — every slot within manifest limits,
/// zero tolerance." Deterministic: builds copy that overflows a 40-character headline slot
/// by a wide margin and asserts <see cref="CopyValidator"/> rejects it.
/// </summary>
public sealed class CopywriterRespectsSlotBudgetsScenario : IEvalScenario
{
    public string Name => "Copywriter respects slot budgets";

    public EvalScenarioKind Kind => EvalScenarioKind.Deterministic;

    public Task<EvalScenarioOutcome> RunAsync(CancellationToken ct)
    {
        var context = new CopyValidator.Context
        {
            Slots = [new CopySlotBrief { Id = "headline", Role = "headline", MaxChars = 40, MaxLines = 2 }],
            AllowedFactKeys = new HashSet<string>(),
        };

        var overflowing = new CopySet
        {
            Slots = [new CopySlot
            {
                Id = "headline",
                Text = "This headline is deliberately far too long to fit inside a forty character budget",
            }],
            FactCitations = [],
        };

        var problems = CopyValidator.Validate(overflowing, context);
        var caught = problems.Any(p => p.Contains("budget", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(caught
            ? EvalScenarioOutcome.Pass($"Rejected as expected: {string.Join("; ", problems)}")
            : EvalScenarioOutcome.Fail(
                "Copy far exceeding its slot's character budget was not rejected — the slot-budget check may have regressed."));
    }
}
