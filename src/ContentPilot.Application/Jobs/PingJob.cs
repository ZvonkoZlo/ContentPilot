using ContentPilot.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// The walking-skeleton job. It proves the whole path — enqueue inside a transaction,
/// lease under contention, dispatch, record, trace — without any AI or rendering.
/// It stays in the codebase as the smoke test for the queue.
/// </summary>
[JobType("ping")]
public sealed record PingJobPayload(string Message, DateTimeOffset RequestedAt);

public sealed class PingJobHandler(ILogger<PingJobHandler> logger, IClock clock)
    : JobHandler<PingJobPayload>
{
    /// <summary>
    /// Records every execution in memory so an integration test can assert a job under
    /// contention ran exactly once. Never used for anything else.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<Guid> Executed = [];

    protected override Task HandleAsync(JobExecutionContext context, PingJobPayload payload, CancellationToken ct)
    {
        Executed.Add(context.JobId);

        var latency = clock.UtcNow - payload.RequestedAt;

        logger.LogInformation(
            "Ping {Message} handled for job {JobId} (tenant {TenantId}, attempt {Attempt}, latency {LatencyMs} ms).",
            payload.Message,
            context.JobId,
            context.TenantId,
            context.Attempt,
            (long)latency.TotalMilliseconds);

        return Task.CompletedTask;
    }
}
