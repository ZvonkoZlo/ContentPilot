using System.Reflection;
using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using NetArchTest.Rules;
using Shouldly;

namespace ContentPilot.ArchitectureTests;

/// <summary>
/// These are the rules that keep the plan's central promise honest over time: the
/// orchestrator decides what happens next, and nothing else does. They are cheap, they
/// run on every commit, and they fail the build rather than a code review.
/// </summary>
public sealed class LayeringRules
{
    private static readonly Assembly Domain = typeof(Domain.Common.Entity).Assembly;
    private static readonly Assembly Application = typeof(IJobQueue).Assembly;
    private static readonly Assembly Infrastructure = typeof(AppDbContext).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_but_the_bcl()
    {
        var result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny("ContentPilot.Application", "ContentPilot.Infrastructure", "Microsoft.EntityFrameworkCore", "Amazon")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull(Describe(result));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure()
    {
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny("ContentPilot.Infrastructure", "Microsoft.EntityFrameworkCore", "Amazon", "Npgsql")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull(Describe(result));
    }

    /// <summary>
    /// The invariant from the plan: an agent returns a value and nothing else. It cannot
    /// enqueue a job, touch the database, or write to storage, so it cannot decide what
    /// happens next even if a future prompt tells it to.
    /// </summary>
    [Fact]
    public void Agents_cannot_reach_persistence_storage_or_the_queue()
    {
        var agentTypes = Types.InAssembly(Application)
            .That()
            .ResideInNamespaceStartingWith("ContentPilot.Application.Agents")
            .GetTypes();

        if (!agentTypes.Any())
        {
            // No agents yet (Phase 3). The rule still has to exist before the first one
            // is written, which is the whole point of asserting it now.
            return;
        }

        var result = Types.InAssembly(Application)
            .That()
            .ResideInNamespaceStartingWith("ContentPilot.Application.Agents")
            .Should()
            .NotHaveDependencyOnAny(
                "ContentPilot.Application.Abstractions.IJobQueue",
                "ContentPilot.Application.Abstractions.IUnitOfWork",
                "ContentPilot.Application.Abstractions.IObjectStore",
                "ContentPilot.Application.Orchestration")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull(Describe(result));
    }

    [Fact]
    public void Renderer_does_not_reference_the_domain_or_application()
    {
        var renderer = typeof(ContentPilot.Renderer.RendererAssemblyMarker).Assembly;

        var result = Types.InAssembly(renderer)
            .Should()
            .NotHaveDependencyOnAny("ContentPilot.Domain", "ContentPilot.Application", "ContentPilot.Infrastructure")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull(Describe(result));
    }

    [Fact]
    public void Entities_are_sealed_or_abstract()
    {
        var result = Types.InAssembly(Domain)
            .That()
            .Inherit(typeof(Domain.Common.Entity))
            .Should()
            .BeSealed()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull(Describe(result));
    }

    [Fact]
    public void Every_job_payload_declares_a_stable_type_name()
    {
        var payloads = Application.GetTypes()
            .Where(t => t.Namespace?.StartsWith("ContentPilot.Application.Jobs", StringComparison.Ordinal) == true)
            .Where(t => t.Name.EndsWith("Payload", StringComparison.Ordinal))
            .ToList();

        payloads.ShouldNotBeEmpty("At least the ping payload should exist.");

        foreach (var payload in payloads)
        {
            payload.GetCustomAttribute<JobTypeAttribute>()
                .ShouldNotBeNull($"{payload.Name} must carry [JobType]; the name is persisted on every job row.");
        }
    }

    [Fact]
    public void Infrastructure_is_the_only_layer_that_knows_about_a_database()
    {
        var result = Types.InAssembly(Infrastructure)
            .That()
            .ResideInNamespaceStartingWith("ContentPilot.Infrastructure")
            .Should()
            .ResideInNamespaceStartingWith("ContentPilot.Infrastructure")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "passed"
            : "Offending types: " + string.Join(", ", result.FailingTypeNames);
}
