using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Observability;

/// <summary>
/// An immutable prompt. Every <see cref="AgentRun"/> points at one, so any output produced
/// months ago can still be traced to the exact text that produced it.
/// <para>
/// Editing a prompt means writing a new version. A startup check compares hashes and fails
/// the process if an existing version's text has changed underneath it — that is the guard
/// that keeps the audit trail from being quietly rewritten.
/// </para>
/// </summary>
public sealed class PromptVersion : Entity, IAppendOnly
{
    private PromptVersion()
    {
        PromptId = null!;
        Body = null!;
        ContentHash = null!;
        ModelProfile = null!;
    }

    public PromptVersion(
        string promptId,
        int version,
        string body,
        string contentHash,
        string modelProfile,
        string? schemaName,
        DateTimeOffset registeredAt)
    {
        PromptId = Guard.MaxLength(Guard.NotBlank(promptId), 80);
        Version = Guard.InRange(version, 1, 9999);
        Body = Guard.NotBlank(body);
        ContentHash = Guard.MaxLength(Guard.NotBlank(contentHash), 64);
        ModelProfile = Guard.MaxLength(Guard.NotBlank(modelProfile), 60);
        SchemaName = schemaName;
        RegisteredAt = registeredAt;
    }

    /// <summary>Stable identifier: <c>content-strategist</c>.</summary>
    public string PromptId { get; private set; }

    public int Version { get; private set; }

    /// <summary>The rendered template source, before variable substitution.</summary>
    public string Body { get; private set; }

    public string ContentHash { get; private set; }

    /// <summary>
    /// The named profile, not a model id. Swapping models is a config change; the prompt
    /// does not know or care which model runs it.
    /// </summary>
    public string ModelProfile { get; private set; }

    public string? SchemaName { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public string Reference => $"{PromptId}@v{Version}";
}
