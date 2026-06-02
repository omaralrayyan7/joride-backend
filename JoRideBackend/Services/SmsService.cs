using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace JoRideBackend.Services;

public interface ISmsService
{
    Task SendSmsAsync(string toPhoneNumber, string message);
}

public class SmsService : ISmsService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmsService> _logger;

    public SmsService(IConfiguration config, ILogger<SmsService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendSmsAsync(string toPhoneNumber, string message)
    {
        var accountSid = _config["Twilio:AccountSid"];
        var authToken  = _config["Twilio:AuthToken"];
        var fromNumber = _config["Twilio:FromPhoneNumber"];

        // ── DEV MODE: Twilio not configured → print OTP to console ───────────
        if (string.IsNullOrWhiteSpace(accountSid) ||
            string.IsNullOrWhiteSpace(authToken)  ||
            string.IsNullOrWhiteSpace(fromNumber))
        {
            _logger.LogWarning("┌─────────────────────────────────────────────┐");
            _logger.LogWarning("│  TWILIO NOT CONFIGURED — DEV MODE ACTIVE     │");
            _logger.LogWarning("│  To: {Phone}", toPhoneNumber);
            _logger.LogWarning("│  Message: {Message}", message);
            _logger.LogWarning("└─────────────────────────────────────────────┘");
            return; // Don't throw — just log and continue
        }

        // ── PRODUCTION: send real SMS via Twilio ──────────────────────────────
        TwilioClient.Init(accountSid, authToken);

        await MessageResource.CreateAsync(
            body: message,
            from: new PhoneNumber(fromNumber),
            to:   new PhoneNumber(toPhoneNumber)
        );
    }
}
