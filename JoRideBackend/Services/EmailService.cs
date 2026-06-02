using System.Net;
using System.Net.Mail;

namespace JoRideBackend.Services;

public interface IEmailService
{
    Task SendAsync(string toEmail, string subject, string body);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string body)
    {
        var host = _config["Smtp:Host"];
        var user = _config["Smtp:User"];
        var pass = _config["Smtp:Password"];

        // ── DEV MODE ──────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(pass))
        {
            _logger.LogWarning("SMTP NOT CONFIGURED — To: {Email} | Body: {Body}", toEmail, body);
            return;
        }

        var fromName = _config["Smtp:FromName"] ?? "JoRide";

        // Try port 587 (STARTTLS) first, then fall back to 465 (SSL)
        var ports = new[] { (587, true), (465, true), (25, false) };

        Exception? lastEx = null;
        foreach (var (port, ssl) in ports)
        {
            try
            {
                _logger.LogInformation("Trying SMTP {Host}:{Port} ssl={Ssl}", host, port, ssl);
                using var client = new SmtpClient(host, port)
                {
                    Credentials = new NetworkCredential(user, pass),
                    EnableSsl   = ssl,
                    Timeout     = 10000,
                };

                var mail = new MailMessage
                {
                    From       = new MailAddress(user, fromName),
                    Subject    = subject,
                    Body       = body,
                    IsBodyHtml = false,
                };
                mail.To.Add(toEmail);

                await client.SendMailAsync(mail);
                _logger.LogInformation("Email sent successfully via port {Port}", port);
                return; // success — stop trying
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Port {Port} failed: {Error}", port, ex.Message);
                lastEx = ex;
            }
        }

        throw new Exception($"All SMTP ports failed. Last error: {lastEx?.Message}");
    }
}
