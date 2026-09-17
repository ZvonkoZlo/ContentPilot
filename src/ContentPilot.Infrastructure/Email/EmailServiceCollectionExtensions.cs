using ContentPilot.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContentPilot.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddContentPilotEmail(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .Validate(options => options.IsValid(out _),
                "Email configuration is invalid. Check Email:ReviewUiBaseUrl and enabled SMTP settings.")
            .ValidateOnStart();

        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();

        return services;
    }
}
