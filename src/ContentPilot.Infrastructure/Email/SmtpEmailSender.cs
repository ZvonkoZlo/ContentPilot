using ContentPilot.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ContentPilot.Infrastructure.Email;

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Email delivery is disabled.");
        }

        var username = settings.Smtp.Username
            ?? throw new InvalidOperationException("Email:Smtp:Username is not configured.");
        var password = settings.Smtp.Password
            ?? throw new InvalidOperationException("Email:Smtp:Password is not configured.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, username));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(username, password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
