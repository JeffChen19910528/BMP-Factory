using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BPM.EmailDelivery;

// Mirrors BPM.Workflow.DependencyInjection's own shape — BPM.Api's composition root only needs to
// call AddNotificationDelivery(), not know about SmtpEmailSender/FakeEmailSender/
// EmailDeliveryWorker directly.
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));

        // Singleton, not Scoped: SmtpEmailSender is stateless (safe either way); FakeEmailSender
        // specifically needs to persist its per-address attempt counter across the worker's
        // separate DI scope per poll tick — a Scoped registration would reset it to zero every
        // tick and could never simulate "fails the first N times, then succeeds" (see its own
        // comment). Neither implementation depends on anything scoped (no BpmDbContext), so
        // singleton lifetime is safe for both.
        var provider = configuration.GetSection(EmailSettings.SectionName)["Provider"];
        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            // Defaults to Fake for any other value (including unset) — a missing/misconfigured
            // Provider must never silently start attempting real SMTP connections with blank
            // credentials (Part J: never accidentally configure the fake provider as production is
            // the inverse risk this guards against too — an explicit "Smtp" opt-in is required).
            services.AddSingleton<IEmailSender, FakeEmailSender>();
        }

        services.AddScoped<NotificationDeliveryProcessor>();
        services.AddHostedService<EmailDeliveryWorker>();

        return services;
    }
}
