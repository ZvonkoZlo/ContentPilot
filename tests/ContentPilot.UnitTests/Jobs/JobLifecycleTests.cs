using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Jobs;
using Shouldly;

namespace ContentPilot.UnitTests.Jobs;

/// <summary>
/// The job lifecycle is the retry story in miniature: every attempt is counted, every
/// terminal state is reachable, and there is no path that loops forever.
/// </summary>
public sealed class JobLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);

    private static Job NewJob(int maxAttempts = 3) =>
        Job.Create("ping", """{"message":"hi"}""", Guid.NewGuid(), Now, null, null, JobPriority.Normal, maxAttempts);

    [Fact]
    public void A_new_job_is_pending_and_runnable_immediately()
    {
        var job = NewJob();

        job.State.ShouldBe(JobState.Pending);
        job.Attempts.ShouldBe(0);
        job.RunAt.ShouldBe(Now);
        job.LeaseUntil.ShouldBeNull();
    }

    [Fact]
    public void Leasing_counts_an_attempt_and_sets_an_expiry()
    {
        var job = NewJob();

        job.Lease("worker-1", Now, TimeSpan.FromMinutes(5));

        job.State.ShouldBe(JobState.Leased);
        job.Attempts.ShouldBe(1);
        job.LockedBy.ShouldBe("worker-1");
        job.LeaseUntil.ShouldBe(Now.AddMinutes(5));
        job.StartedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_job_cannot_be_leased_twice()
    {
        var job = NewJob();
        job.Lease("worker-1", Now, TimeSpan.FromMinutes(5));

        // The database lock makes this unreachable in practice; the guard makes a bug in
        // the dispatcher loud instead of silently double-processing.
        Should.Throw<InvalidOperationException>(() => job.Lease("worker-2", Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Success_clears_the_lease_and_the_last_error()
    {
        var job = NewJob();
        job.Lease("worker-1", Now, TimeSpan.FromMinutes(5));
        job.Fail(Now, "transient blip", TimeSpan.FromSeconds(10));
        job.Lease("worker-1", Now.AddSeconds(10), TimeSpan.FromMinutes(5));

        job.Succeed(Now.AddSeconds(11));

        job.State.ShouldBe(JobState.Succeeded);
        job.LastError.ShouldBeNull();
        job.LeaseUntil.ShouldBeNull();
        job.LockedBy.ShouldBeNull();
        job.CompletedAt.ShouldBe(Now.AddSeconds(11));
    }

    [Fact]
    public void Failure_returns_to_pending_with_backoff_while_attempts_remain()
    {
        var job = NewJob(maxAttempts: 3);
        job.Lease("worker-1", Now, TimeSpan.FromMinutes(5));

        job.Fail(Now, "provider 429", TimeSpan.FromSeconds(30));

        job.State.ShouldBe(JobState.Pending);
        job.RunAt.ShouldBe(Now.AddSeconds(30));
        job.Attempts.ShouldBe(1);
        job.LastError.ShouldBe("provider 429");
        job.LeaseUntil.ShouldBeNull();
    }

    [Fact]
    public void Failure_after_the_last_attempt_is_terminal()
    {
        var job = NewJob(maxAttempts: 2);

        job.Lease("w", Now, TimeSpan.FromMinutes(5));
        job.Fail(Now, "boom", TimeSpan.FromSeconds(1));
        job.Lease("w", Now.AddSeconds(1), TimeSpan.FromMinutes(5));
        job.Fail(Now.AddSeconds(2), "boom again", TimeSpan.FromSeconds(1));

        job.State.ShouldBe(JobState.Dead);
        job.Attempts.ShouldBe(2);
        job.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void A_permanent_failure_skips_the_remaining_attempts()
    {
        var job = NewJob(maxAttempts: 5);
        job.Lease("w", Now, TimeSpan.FromMinutes(5));

        // Retrying work that can never succeed only burns time and money.
        job.Fail(Now, "no handler registered", TimeSpan.FromSeconds(30), permanent: true);

        job.State.ShouldBe(JobState.Dead);
        job.Attempts.ShouldBe(1);
    }

    [Fact]
    public void An_expired_lease_is_reclaimed_as_a_failed_attempt()
    {
        var job = NewJob();
        job.Lease("crashed-worker", Now, TimeSpan.FromMinutes(5));

        job.ReclaimExpiredLease(Now.AddMinutes(6), TimeSpan.FromSeconds(15));

        job.State.ShouldBe(JobState.Pending);
        job.RunAt.ShouldBe(Now.AddMinutes(6).AddSeconds(15));
        job.LastError.ShouldNotBeNull();
        job.LastError.ShouldContain("Lease expired");
    }

    [Fact]
    public void Reclaiming_a_job_that_is_not_leased_does_nothing()
    {
        var job = NewJob();

        job.ReclaimExpiredLease(Now.AddHours(1), TimeSpan.FromSeconds(15));

        job.State.ShouldBe(JobState.Pending);
        job.Attempts.ShouldBe(0);
        job.LastError.ShouldBeNull();
    }

    [Fact]
    public void Extending_a_lease_pushes_the_expiry_out()
    {
        var job = NewJob();
        job.Lease("w", Now, TimeSpan.FromMinutes(5));

        job.ExtendLease(Now.AddMinutes(4), TimeSpan.FromMinutes(5));

        job.LeaseUntil.ShouldBe(Now.AddMinutes(9));
    }

    [Fact]
    public void A_long_error_is_truncated_rather_than_rejected()
    {
        var job = NewJob();
        job.Lease("w", Now, TimeSpan.FromMinutes(5));

        job.Fail(Now, new string('x', 10_000), TimeSpan.FromSeconds(1));

        job.LastError!.Length.ShouldBe(4000);
    }
}
