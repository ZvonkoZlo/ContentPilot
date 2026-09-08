namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The transactional work queue. Enqueueing participates in the caller's transaction,
/// so a job never exists for a state change that was rolled back — and a state change
/// never lands without the job that was supposed to follow it.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Schedules a job. When <paramref name="idempotencyKey"/> is supplied and already
    /// present, the existing job identifier is returned and nothing new is queued —
    /// this is what makes a double-fired cron harmless.
    /// </summary>
    Task<Guid> EnqueueAsync<TPayload>(
        TPayload payload,
        Guid? tenantId = null,
        DateTimeOffset? runAt = null,
        string? idempotencyKey = null,
        JobPriority priority = JobPriority.Normal,
        CancellationToken ct = default)
        where TPayload : notnull;
}

/// <summary>
/// Manual triggers outrank scheduled batches: a user waiting for "Generate now" should
/// never queue behind Monday's cron run.
/// </summary>
public enum JobPriority
{
    High = 0,
    Normal = 50,
    Low = 100,
}

/// <summary>
/// Names the queue type for a payload. The name is persisted on every job row, so it is
/// part of the wire contract — rename it and in-flight jobs stop dispatching.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class JobTypeAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Job type name must not be blank.", nameof(name))
        : name.Trim();
}

public static class JobTypeName
{
    public static string For<TPayload>() => For(typeof(TPayload));

    public static string For(Type payloadType)
    {
        var attribute = (JobTypeAttribute?)Attribute.GetCustomAttribute(payloadType, typeof(JobTypeAttribute));

        return attribute?.Name ?? throw new InvalidOperationException(
            $"{payloadType.Name} has no [JobType] attribute. Every queued payload must declare a stable type name.");
    }
}

/// <summary>
/// What a handler knows about the execution it is running inside.
/// </summary>
public sealed record JobExecutionContext(
    Guid JobId,
    string JobType,
    Guid? TenantId,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset LeasedAt)
{
    /// <summary>
    /// Pushes the lease out for long-running work (a render, a packaging run) so the
    /// reaper does not reclaim a job that is making progress.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> ExtendLease { get; init; } =
        static (_, _) => Task.CompletedTask;
}

/// <summary>
/// Non-generic dispatch surface used by the worker loop.
/// </summary>
public interface IJobHandler
{
    string JobType { get; }

    Task HandleAsync(JobExecutionContext context, string payloadJson, CancellationToken ct);
}

/// <summary>
/// Base class handlers actually derive from: it owns deserialisation so a handler only
/// ever sees a typed payload.
/// </summary>
public abstract class JobHandler<TPayload> : IJobHandler
    where TPayload : notnull
{
    public string JobType => JobTypeName.For<TPayload>();

    public Task HandleAsync(JobExecutionContext context, string payloadJson, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<TPayload>(payloadJson, JobSerialization.Options)
            ?? throw new InvalidOperationException($"Job {context.JobId} carried a null {typeof(TPayload).Name} payload.");

        return HandleAsync(context, payload, ct);
    }

    protected abstract Task HandleAsync(JobExecutionContext context, TPayload payload, CancellationToken ct);
}

public static class JobSerialization
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Thrown by a handler when the work can never succeed as queued: bad configuration, a
/// missing required asset, a payload referencing a deleted row. The job goes straight to
/// Dead without burning its remaining attempts on a guaranteed failure.
/// </summary>
public sealed class PermanentJobFailureException(string message, Exception? inner = null)
    : Exception(message, inner);
