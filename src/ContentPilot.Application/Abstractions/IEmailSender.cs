namespace ContentPilot.Application.Abstractions;

/// <summary>Sends one plain-text transactional email through the configured transport.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
