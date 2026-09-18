using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace BPM.EmailDelivery;

// Phase 6.2 — the production IEmailSender, built on .NET's own System.Net.Mail.SmtpClient (no new
// NuGet dependency — Part AA/J: "do NOT add a large infrastructure dependency merely for this
// phase"). Configuration is entirely external (EmailSettings, bound from appsettings/environment
// — see DependencyInjection.cs); no host/credential is ever hard-coded here.
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailSettings _settings;

    public SmtpEmailSender(IOptions<EmailSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.UseSsl,
            Credentials = string.IsNullOrEmpty(_settings.Username) ? null : new NetworkCredential(_settings.Username, _settings.Password),
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_settings.FromAddress, _settings.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(toAddress);

        // A plain-text alternate view alongside the HTML body (Part K: "Prefer HTML + plain text
        // if easy within existing architecture") — most mail clients pick whichever they render
        // best; this costs nothing extra to attach.
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));

        // SmtpClient has no cancellationToken-accepting overload — SendMailAsync is the closest
        // available async API; a hung connection is still bounded by SmtpClient's own Timeout.
        await client.SendMailAsync(message);
    }
}
