using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Infrastructure.Jobs;
using Shouldly;

namespace ContentPilot.UnitTests.Jobs;

public sealed class JobQueueOptionsTests
{
    [Fact]
    public void Backoff_grows_with_each_attempt()
    {
        var options = new JobQueueOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(10),
            RetryMaxDelay = TimeSpan.FromMinutes(10),
        };

        var first = options.CalculateRetryDelay(1);
        var third = options.CalculateRetryDelay(3);

        // Jitter is +/-15%, so compare against bands rather than exact values.
        first.ShouldBeInRange(TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(11.5));
        third.ShouldBeInRange(TimeSpan.FromSeconds(34), TimeSpan.FromSeconds(46));
    }

    [Fact]
    public void Backoff_is_capped()
    {
        var options = new JobQueueOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(10),
            RetryMaxDelay = TimeSpan.FromMinutes(10),
        };

        // Without a cap, attempt 20 would be several days out and the job would look lost.
        options.CalculateRetryDelay(20).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(11.5));
    }

    [Fact]
    public void Backoff_is_jittered_so_retries_do_not_synchronise()
    {
        var options = new JobQueueOptions { RetryBaseDelay = TimeSpan.FromSeconds(10) };

        var samples = Enumerable.Range(0, 30).Select(_ => options.CalculateRetryDelay(2)).Distinct().ToList();

        samples.Count.ShouldBeGreaterThan(1,
            "Identical delays mean a provider outage produces a synchronised retry stampede.");
    }
}

public sealed class JobTypeNameTests
{
    [Fact]
    public void A_declared_job_type_resolves_to_its_stable_name() =>
        JobTypeName.For<PingJobPayload>().ShouldBe("ping");

    [Fact]
    public void A_payload_without_the_attribute_fails_loudly()
    {
        // Silent fallback to a type name would make a later rename break in-flight jobs
        // with no warning, so this throws instead.
        var ex = Should.Throw<InvalidOperationException>(() => JobTypeName.For<UndeclaredPayload>());

        ex.Message.ShouldContain("[JobType]");
    }

    private sealed record UndeclaredPayload(string Value);
}
