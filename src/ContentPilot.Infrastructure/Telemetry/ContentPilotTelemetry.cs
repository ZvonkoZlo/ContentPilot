using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ContentPilot.Infrastructure.Telemetry;

/// <summary>
/// The single activity source and meter for the platform. Every workflow, agent, render
/// and job span hangs off this, which is what makes one campaign readable as one trace.
/// </summary>
public static class ContentPilotTelemetry
{
    public const string ServiceName = "contentpilot";

    public static readonly ActivitySource ActivitySource = new(ServiceName, "0.1.0");

    public static readonly Meter Meter = new(ServiceName, "0.1.0");

    /// <summary>Jobs that reached a terminal state, tagged by type and outcome.</summary>
    public static readonly Counter<long> JobsCompleted =
        Meter.CreateCounter<long>("contentpilot.jobs.completed", unit: "{job}");

    public static readonly Histogram<double> JobDuration =
        Meter.CreateHistogram<double>("contentpilot.jobs.duration", unit: "ms");
}
