using JoRideBackend.Services;

namespace JoRideBackend.Tests.Fakes;

/// <summary>Test-only ISmsService that records calls instead of hitting Twilio.</summary>
public class FakeSmsService : ISmsService
{
    public List<(string To, string Message)> SentMessages { get; } = new();

    public Task SendSmsAsync(string toPhoneNumber, string message)
    {
        SentMessages.Add((toPhoneNumber, message));
        return Task.CompletedTask;
    }
}
