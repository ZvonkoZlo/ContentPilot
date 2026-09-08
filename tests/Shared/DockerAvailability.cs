using System.Diagnostics;

namespace ContentPilot.TestSupport;

/// <summary>
/// Integration and workflow tests run against real Postgres and real MinIO — an in-memory
/// provider would not exercise FOR UPDATE SKIP LOCKED, partial indexes, jsonb or presigned
/// URLs, which is most of what those tests exist to prove.
/// <para>
/// When no Docker daemon is reachable they skip rather than fail, so a machine without
/// Docker still gets a green unit and architecture suite. CI always has Docker, so the
/// skip never hides a regression there.
/// </para>
/// This file is linked into both test projects rather than duplicated.
/// </summary>
public static class DockerAvailability
{
    public const string SkipReason =
        "Docker is not reachable. Start Docker Desktop, then re-run these tests.";

    private static readonly Lazy<bool> Probe = new(CheckDaemon, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Probe.Value;

    private static bool CheckDaemon()
    {
        try
        {
            // `docker info` exits 0 even when the daemon is down, printing an error and
            // an empty server version. `docker version` reports the server honestly, and
            // the output is checked as well so a future CLI change cannot fool us.
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();

            return process.WaitForExit(10_000)
                && process.ExitCode == 0
                && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            // Docker CLI not installed at all.
            return false;
        }
    }
}

/// <summary>A fact that skips itself when Docker is unavailable.</summary>
public sealed class DockerFactAttribute : Xunit.FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>A theory that skips itself when Docker is unavailable.</summary>
public sealed class DockerTheoryAttribute : Xunit.TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
