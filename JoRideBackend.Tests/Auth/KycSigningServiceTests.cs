using JoRideBackend.Services.Auth;
using Microsoft.Extensions.Configuration;

namespace JoRideBackend.Tests.Auth;

public class KycSigningServiceTests
{
    private static KycSigningService CreateSut(string? secret) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["KYC_DOCUMENT_SIGNING_SECRET"] = secret })
            .Build());

    [Fact]
    public void Sign_then_Verify_round_trips_successfully()
    {
        var service = CreateSut("test-secret-value");
        var documentId = Guid.NewGuid();

        var (signature, expires) = service.Sign(documentId, TimeSpan.FromMinutes(10));

        Assert.True(service.Verify(documentId, expires, signature));
    }

    [Fact]
    public void Verify_fails_for_a_different_document_id()
    {
        var service = CreateSut("test-secret-value");
        var (signature, expires) = service.Sign(Guid.NewGuid(), TimeSpan.FromMinutes(10));

        Assert.False(service.Verify(Guid.NewGuid(), expires, signature));
    }

    [Fact]
    public void Verify_fails_for_a_tampered_signature()
    {
        var service = CreateSut("test-secret-value");
        var documentId = Guid.NewGuid();
        var (signature, expires) = service.Sign(documentId, TimeSpan.FromMinutes(10));

        var tampered = signature[..^2] + (signature[^2] == 'A' ? "B" : "A") + signature[^1];

        Assert.False(service.Verify(documentId, expires, tampered));
    }

    [Fact]
    public void Verify_fails_once_expired()
    {
        var service = CreateSut("test-secret-value");
        var documentId = Guid.NewGuid();
        var (signature, _) = service.Sign(documentId, TimeSpan.FromMinutes(10));

        var alreadyExpired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        Assert.False(service.Verify(documentId, alreadyExpired, signature));
    }

    [Fact]
    public void Verify_fails_when_signed_with_a_different_secret()
    {
        var signer = CreateSut("secret-one");
        var verifier = CreateSut("secret-two");
        var documentId = Guid.NewGuid();

        var (signature, expires) = signer.Sign(documentId, TimeSpan.FromMinutes(10));

        Assert.False(verifier.Verify(documentId, expires, signature));
    }

    [Fact]
    public void IsConfigured_is_false_without_a_secret()
    {
        var service = CreateSut(null);

        Assert.False(service.IsConfigured);
        Assert.False(service.Verify(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), "anything"));
    }

    [Fact]
    public void Sign_throws_when_not_configured()
    {
        var service = CreateSut(null);

        Assert.Throws<InvalidOperationException>(() => service.Sign(Guid.NewGuid(), TimeSpan.FromMinutes(10)));
    }
}
