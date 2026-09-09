using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Observability;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Runs an agent: renders its prompt, calls the model, parses and validates the result,
/// repairs it if it fails, and records what happened.
/// <para>
/// Every side effect lives here so agents can stay pure. The repair loop is shared rather
/// than reimplemented per agent, which matters because getting it slightly wrong in four
/// places is how a pipeline quietly starts costing four times what it should.
/// </para>
/// </summary>
public sealed class AgentExecutor(
    ILanguageModelClient client,
    PromptLibrary prompts,
    PromptRegistry registry,
    AppDbContext db,
    IObjectStore store,
    ITenantContext tenant,
    IClock clock,
    ILogger<AgentExecutor> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Two attempts, not more. A validator rejection means the model misread the brief;
    /// once fed the exact problems it usually fixes them, and if it does not, a third
    /// identical request rarely helps and always bills.
    /// </summary>
    public const int MaxRepairAttempts = 2;

    public async Task<AgentResult<TOutput>> RunAsync<TInput, TOutput>(
        IAgent<TInput, TOutput> agent,
        TInput input,
        AgentContext context,
        CancellationToken ct = default)
    {
        var prompt = prompts.Get(agent.PromptId);
        var promptVersionId = await registry.ResolveAsync(prompt, ct);
        var (system, user) = prompt.Render(agent.BuildVariables(input));

        var repaired = new List<string>();
        long spent = 0;

        for (var attempt = 1; attempt <= MaxRepairAttempts; attempt++)
        {
            var run = new AgentRun(
                tenant.RequireTenantId(), context.CampaignId, context.ContentItemId,
                agent.Name, agent.Version, promptVersionId, "unknown", attempt, clock.UtcNow);

            db.AgentRuns.Add(run);

            var activity = Activity.Current;
            run.AttachTrace(activity?.TraceId.ToString(), activity?.SpanId.ToString());

            var started = Stopwatch.GetTimestamp();

            try
            {
                var response = await client.CompleteAsync(new LlmRequest
                {
                    Profile = prompt.ModelProfile,
                    System = system,
                    User = user,
                    ResponseSchema = agent.ResponseSchema,
                    Operation = $"{agent.Name}-{attempt}",
                    CampaignId = context.CampaignId,
                    ContentItemId = context.ContentItemId,
                }, ct);

                spent += response.CostMicroCents;
                var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var usage = new TokenUsageSnapshot(
                    response.Usage.InputTokens, response.Usage.OutputTokens,
                    response.Usage.CacheReadTokens, response.Usage.CacheWriteTokens);

                await ArchiveAsync(run, context, agent.Name, attempt, system, user, response.Json, ct);

                if (response.RefusalCategory is { } refusal)
                {
                    run.FailValidation(usage, response.CostMicroCents, durationMs,
                        $"The model declined ({refusal}).", clock.UtcNow);

                    await db.SaveChangesAsync(ct);

                    throw new AgentValidationException(agent.Name, [$"The model declined ({refusal})."]);
                }

                var output = Deserialize<TOutput>(agent.Name, response.Json);
                var problems = agent.Validate(output, input);

                if (problems.Count == 0)
                {
                    run.Succeed(usage, response.CostMicroCents, durationMs, clock.UtcNow);
                    await db.SaveChangesAsync(ct);

                    return new AgentResult<TOutput>
                    {
                        Value = output,
                        PromptReference = prompt.Reference,
                        ModelId = response.ModelId,
                        Attempts = attempt,
                        CostMicroCents = spent,
                        RepairedProblems = repaired,
                    };
                }

                run.FailValidation(usage, response.CostMicroCents, durationMs,
                    string.Join("; ", problems), clock.UtcNow);

                await db.SaveChangesAsync(ct);

                logger.LogWarning(
                    "{Agent} attempt {Attempt} failed validation: {Problems}",
                    agent.Name, attempt, string.Join("; ", problems));

                repaired.AddRange(problems);

                if (attempt == MaxRepairAttempts)
                {
                    throw new AgentValidationException(agent.Name, problems);
                }

                // The exact problems, appended to the original request rather than sent as
                // a fresh conversation: the model keeps its own previous reasoning in view,
                // and the cached system prefix still applies.
                user = Repair(user, problems);
            }
            catch (Exception ex) when (ex is not AgentValidationException)
            {
                run.Fail(ex.Message, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, clock.UtcNow);
                await db.SaveChangesAsync(ct);

                throw;
            }
        }

        throw new UnreachableException();
    }

    private static string Repair(string user, IReadOnlyList<string> problems)
    {
        var builder = new StringBuilder(user);

        builder.Append("\n\n## Your previous answer was rejected\n\n");
        builder.Append("These are mechanical checks, not opinions. Fix every one:\n\n");

        foreach (var problem in problems)
        {
            builder.Append("- ").Append(problem).Append('\n');
        }

        builder.Append("\nReturn a complete replacement, not a patch.");

        return builder.ToString();
    }

    private static TOutput Deserialize<TOutput>(string agentName, string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TOutput>(json, Json)
                ?? throw new LlmSchemaException($"{agentName} returned JSON null.");
        }
        catch (JsonException ex)
        {
            // The provider enforces the schema, so this means the schema and the type have
            // drifted apart - a bug on our side, not the model's.
            throw new LlmSchemaException(
                $"{agentName} returned JSON that does not match {typeof(TOutput).Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Prompts and responses go to object storage, not Postgres. They are large, read
    /// rarely, and only when something has gone wrong — keeping them in the database would
    /// make the table that answers "what did this cost" too heavy to query.
    /// </summary>
    private async Task ArchiveAsync(
        AgentRun run, AgentContext context, string agentName, int attempt,
        string system, string user, string output, CancellationToken ct)
    {
        try
        {
            var prefix = $"campaigns/{context.CampaignId:N}/agents/{agentName}/{run.Id:N}-{attempt}";

            var inputKey = ObjectKey.ForTenant(tenant.RequireTenantId(), $"{prefix}-input.txt");
            var outputKey = ObjectKey.ForTenant(tenant.RequireTenantId(), $"{prefix}-output.json");

            await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes($"{system}\n\n---\n\n{user}")))
            {
                await store.PutAsync(inputKey, stream, "text/plain; charset=utf-8", ct);
            }

            await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(output)))
            {
                await store.PutAsync(outputKey, stream, "application/json", ct);
            }

            run.AttachPayloads(inputKey.Value, outputKey.Value);
        }
        catch (Exception ex)
        {
            // Losing the archive is bad; losing the run because the archive failed is worse.
            logger.LogWarning(ex, "Could not archive payloads for {Agent} run {RunId}.", agentName, run.Id);
        }
    }
}

public sealed record AgentContext
{
    public required Guid CampaignId { get; init; }

    /// <summary>Null for campaign-level agents such as the strategist.</summary>
    public Guid? ContentItemId { get; init; }
}
