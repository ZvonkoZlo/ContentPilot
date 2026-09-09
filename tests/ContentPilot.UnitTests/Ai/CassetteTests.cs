using ContentPilot.Application.Ai;
using ContentPilot.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ContentPilot.UnitTests.Ai;

/// <summary>
/// Cassettes are what let the agent tests exercise real prompts and real validators against
/// real model output, with no key and no spend. They only work if a replay is exact and a
/// miss is loud.
/// </summary>
public sealed class CassetteTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cassettes-{Guid.NewGuid():N}");

    [Fact]
    public async Task Recording_then_replaying_returns_the_same_answer()
    {
        var request = Request("plan-week");
        var live = new StubClient("""{"theme":"Stop losing bookings"}""");

        var recorded = await Client(live, CassetteMode.Record).CompleteAsync(request);
        recorded.FromCassette.ShouldBeFalse();

        var offline = new StubClient("SHOULD NOT BE CALLED");
        var replayed = await Client(offline, CassetteMode.Replay).CompleteAsync(request);

        replayed.Json.ShouldBe(recorded.Json);
        replayed.ModelId.ShouldBe(recorded.ModelId);
        replayed.Usage.InputTokens.ShouldBe(recorded.Usage.InputTokens);
        replayed.FromCassette.ShouldBeTrue();
        offline.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_missing_cassette_is_an_error_rather_than_a_live_call()
    {
        var live = new StubClient("{}");

        // Falling through to the provider would turn a CI run into a bill and make a green
        // suite depend on the network.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Client(live, CassetteMode.Replay).CompleteAsync(Request("never-recorded")));

        ex.Message.ShouldContain("No cassette");
        live.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_changed_prompt_misses_rather_than_replaying_a_stale_answer()
    {
        await Client(new StubClient("""{"a":1}"""), CassetteMode.Record)
            .CompleteAsync(Request("plan-week", user: "original question"));

        // Keyed by content, so an edited prompt must not be answered from an answer to a
        // question nobody is asking any more.
        await Should.ThrowAsync<InvalidOperationException>(
            () => Client(new StubClient("{}"), CassetteMode.Replay)
                .CompleteAsync(Request("plan-week", user: "a different question")));
    }

    [Fact]
    public async Task Cassettes_off_always_reaches_the_provider()
    {
        var live = new StubClient("""{"ok":true}""");

        await Client(live, CassetteMode.Off).CompleteAsync(Request("plan-week"));
        await Client(live, CassetteMode.Off).CompleteAsync(Request("plan-week"));

        live.Calls.ShouldBe(2);
        Directory.Exists(_directory).ShouldBeFalse();
    }

    [Fact]
    public async Task A_recorded_cassette_is_readable_by_a_person()
    {
        await Client(new StubClient("""{"theme":"x"}"""), CassetteMode.Record)
            .CompleteAsync(Request("plan-week", system: "You are a content strategist."));

        var file = Directory.GetFiles(_directory).ShouldHaveSingleItem();
        var text = await File.ReadAllTextAsync(file);

        // A prompt change should show up in the diff, not only as a changed hash.
        text.ShouldContain("You are a content strategist.");
        text.ShouldContain("plan-week");
    }

    private CassetteLanguageModelClient Client(ILanguageModelClient inner, CassetteMode mode) =>
        new(inner,
            Options.Create(new AiOptions { Cassettes = mode, CassetteDirectory = _directory }),
            NullLogger<CassetteLanguageModelClient>.Instance);

    private static LlmRequest Request(string operation, string? system = null, string? user = null) => new()
    {
        Profile = "strategist",
        System = system ?? "You are a strategist.",
        User = user ?? "Plan a week.",
        ResponseSchema = """{"type":"object"}""",
        Operation = operation,
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StubClient(string json) : ILanguageModelClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult(new LlmResponse
            {
                Json = json,
                ModelId = "claude-opus-5",
                Usage = new TokenUsage(1200, 340, 0, 0),
                DurationMs = 900,
                CostMicroCents = 145_000,
            });
        }
    }
}
