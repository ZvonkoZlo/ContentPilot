namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The only source of time in the application. Workflow deadlines, job leases and
/// retention all depend on it, so every one of them is testable without waiting.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
