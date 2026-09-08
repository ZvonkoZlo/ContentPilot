using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Common;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// A queued unit of work. Deliberately a persistence concern rather than a domain
/// concept: it lives in the same database as the state it advances so that the job
/// outcome, the workflow transition and the follow-up job all commit together.
/// </summary>
public sealed class Job : Entity
{
    private Job()
    {
        Type = null!;
        PayloadJson = null!;
    }

    private Job(string type, string payloadJson, Guid? tenantId, DateTimeOffset runAt, string? idempotencyKey, JobPriority priority, int maxAttempts)
    {
        Type = Guard.NotBlank(type);
        PayloadJson = Guard.NotBlank(payloadJson);
        TenantId = tenantId;
        RunAt = runAt;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        Priority = (int)priority;
        MaxAttempts = Guard.InRange(maxAttempts, 1, 25);
        State = JobState.Pending;
    }

    public string Type { get; private set; }

    public string PayloadJson { get; private set; }

    /// <summary>Null for maintenance jobs that legitimately span tenants.</summary>
    public Guid? TenantId { get; private set; }

    public DateTimeOffset RunAt { get; private set; }

    public JobState State { get; private set; }

    public int Priority { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? LockedBy { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Unique when present. This is what absorbs a double-fired trigger.</summary>
    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static Job Create(
        string type,
        string payloadJson,
        Guid? tenantId,
        DateTimeOffset now,
        DateTimeOffset? runAt,
        string? idempotencyKey,
        JobPriority priority,
        int maxAttempts)
    {
        var job = new Job(type, payloadJson, tenantId, runAt ?? now, idempotencyKey, priority, maxAttempts)
        {
            CreatedAt = now,
        };

        return job;
    }

    public void Lease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (State != JobState.Pending)
        {
            throw new InvalidOperationException($"Job {Id} cannot be leased from state {State}.");
        }

        State = JobState.Leased;
        Attempts++;
        LockedBy = Guard.NotBlank(workerId);
        LeaseUntil = now + leaseDuration;
        StartedAt ??= now;
    }

    public void ExtendLease(DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (State != JobState.Leased)
        {
            return;
        }

        LeaseUntil = now + leaseDuration;
    }

    public void Succeed(DateTimeOffset now)
    {
        State = JobState.Succeeded;
        CompletedAt = now;
        LeaseUntil = null;
        LockedBy = null;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt. Returns to Pending with backoff while attempts remain,
    /// otherwise becomes Dead and visible in the admin view. There is no third outcome.
    /// </summary>
    public void Fail(DateTimeOffset now, string error, TimeSpan retryDelay, bool permanent = false)
    {
        LastError = Truncate(error, 4000);
        LeaseUntil = null;
        LockedBy = null;

        if (permanent || Attempts >= MaxAttempts)
        {
            State = JobState.Dead;
            CompletedAt = now;
            return;
        }

        State = JobState.Pending;
        RunAt = now + retryDelay;
    }

    /// <summary>Called by the reaper for a lease that expired with no outcome.</summary>
    public void ReclaimExpiredLease(DateTimeOffset now, TimeSpan retryDelay)
    {
        if (State != JobState.Leased)
        {
            return;
        }

        Fail(now, $"Lease expired at {LeaseUntil:O} without an outcome (worker {LockedBy ?? "unknown"}).", retryDelay);
    }

    public void Cancel(DateTimeOffset now, string reason)
    {
        State = JobState.Cancelled;
        CompletedAt = now;
        LeaseUntil = null;
        LockedBy = null;
        LastError = Truncate(reason, 4000);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public enum JobState
{
    Pending = 0,
    Leased = 1,
    Succeeded = 2,

    /// <summary>Attempts exhausted or permanently failed. Requires a human.</summary>
    Dead = 3,
    Cancelled = 4,
}
