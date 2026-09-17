namespace ContentPilot.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Master switch. Disabled by default so local/test runs never send mail.</summary>
    public bool Enabled { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Browser-visible root used for the durable campaign review link.</summary>
    public string ReviewUiBaseUrl { get; set; } = "http://localhost:4200";

    public string FromName { get; set; } = "ContentPilot";

    public bool IsValid(out string? error)
    {
        error = null;

        if (!Uri.TryCreate(ReviewUiBaseUrl, UriKind.Absolute, out var reviewUri) ||
            (reviewUri.Scheme != Uri.UriSchemeHttp && reviewUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Email:ReviewUiBaseUrl must be an absolute http/https URL.";
            return false;
        }

        if (!Enabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(Smtp.Host) || Smtp.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Smtp.Username) || string.IsNullOrWhiteSpace(Smtp.Password))
        {
            error = "Email SMTP host, port, username and password are required when Email:Enabled is true.";
            return false;
        }

        return true;
    }
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "smtp-mail.outlook.com";

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }
}
