using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.Webhooks;

public class HyperPayWebhookServiceTests
{
    private static (PaymentsDbContext Db, HyperPayWebhookService Service, string Secret) CreateSut()
    {
        var secret = HyperPayWebhookTestHelper.RandomSecretHex();
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var config = BuildConfig(secret);
        var service = new HyperPayWebhookService(db, config, NullLogger<HyperPayWebhookService>.Instance);
        return (db, service, secret);
    }

    private static IConfiguration BuildConfig(string? secret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HYPERPAY_WEBHOOK_SECRET"] = secret })
            .Build();

    private static PaymentIntent SeedIntent(PaymentsDbContext db, PaymentIntentState state, string? providerRef = null)
    {
        var intent = new PaymentIntent
        {
            Id = Guid.NewGuid(),
            Amount = 10m,
            Currency = "USD",
            UserId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        if (state != PaymentIntentState.Created)
        {
            intent.TransitionTo(PaymentIntentState.Authorized);
        }
        if (state == PaymentIntentState.Captured)
        {
            intent.TransitionTo(PaymentIntentState.Captured);
        }
        intent.ProviderRef = providerRef;
        db.PaymentIntents.Add(intent);
        db.SaveChanges();
        return intent;
    }

    private static string BuildPayload(
        string id, string? resourcePath, string paymentType, string resultCode, string? merchantTransactionId = null) =>
        $$"""
        {
          "type": "PAYMENT",
          "payload": {
            "id": "{{id}}",
            "resourcePath": {{(resourcePath is null ? "null" : $"\"{resourcePath}\"")}},
            "paymentType": "{{paymentType}}",
            "merchantTransactionId": {{(merchantTransactionId is null ? "null" : $"\"{merchantTransactionId}\"")}},
            "result": { "code": "{{resultCode}}" }
          }
        }
        """;

    // ── Valid new event ──────────────────────────────────────────────────

    [Fact]
    public async Task First_authorize_webhook_is_matched_via_merchantTransactionId_and_authorizes_the_intent()
    {
        var (db, service, secret) = CreateSut();
        // Realistic: freshly created intent has no ProviderRef yet — only merchantTransactionId
        // (set on checkout creation, per HyperPayGateway.CreateCheckoutAsync) can match it.
        var intent = SeedIntent(db, PaymentIntentState.Created);

        var json = BuildPayload("txn-1", "/v1/payments/txn-1", "PA", "000.000.000", intent.Id.ToString());
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        var result = await service.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.Accepted, result.Outcome);
        var reloaded = await db.PaymentIntents.FindAsync(intent.Id);
        Assert.Equal(PaymentIntentState.Authorized, reloaded!.State);
        Assert.Equal("/v1/payments/txn-1", reloaded.ProviderRef); // recorded for future CP/RV/RF
    }

    [Fact]
    public async Task Capture_webhook_matched_via_ProviderRef_transitions_Authorized_to_Captured()
    {
        var (db, service, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-2";
        var intent = SeedIntent(db, PaymentIntentState.Authorized, providerRef);

        var json = BuildPayload("txn-2", providerRef, "CP", "000.000.000");
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        var result = await service.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.Accepted, result.Outcome);
        Assert.Equal(PaymentIntentState.Captured, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
    }

    [Fact]
    public async Task Failure_result_code_transitions_intent_to_Failed()
    {
        var (db, service, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-3";
        var intent = SeedIntent(db, PaymentIntentState.Authorized, providerRef);

        var json = BuildPayload("txn-3", providerRef, "CP", "800.100.151"); // a real OPP-style decline code
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        var result = await service.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.Accepted, result.Outcome);
        Assert.Equal(PaymentIntentState.Failed, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
    }

    // ── Idempotency ──────────────────────────────────────────────────────

    [Fact]
    public async Task Replayed_event_is_a_no_op_and_does_not_double_transition()
    {
        var (db, service, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-replay";
        var intent = SeedIntent(db, PaymentIntentState.Authorized, providerRef);

        var json = BuildPayload("txn-replay", providerRef, "CP", "000.000.000");
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        var first = await service.ProcessAsync(bodyHex, ivHex, tagHex);
        Assert.Equal(HyperPayWebhookOutcome.Accepted, first.Outcome);
        Assert.Equal(PaymentIntentState.Captured, (await db.PaymentIntents.FindAsync(intent.Id))!.State);

        // Exact same (ciphertext, IV, tag) redelivered by the provider — must be a safe no-op.
        // (A second real transition attempt would throw: Captured->Captured is illegal.)
        var second = await service.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(PaymentIntentState.Captured, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
        Assert.Equal(1, await db.ProcessedPaymentEvents.CountAsync());
    }

    // ── Invalid / unverifiable events ───────────────────────────────────

    [Fact]
    public async Task Wrong_secret_is_rejected_without_touching_any_PaymentIntent()
    {
        var (db, _, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-wrongsecret";
        var intent = SeedIntent(db, PaymentIntentState.Authorized, providerRef);
        var stateBefore = intent.State;

        var json = BuildPayload("txn-wrongsecret", providerRef, "CP", "000.000.000");
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        // A service configured with a DIFFERENT secret than the one used to encrypt — the
        // realistic "wrong/unverifiable" case (e.g. a forged request, or misconfiguration).
        var serviceWithWrongSecret = new HyperPayWebhookService(
            db, BuildConfig(HyperPayWebhookTestHelper.RandomSecretHex()), NullLogger<HyperPayWebhookService>.Instance);

        var result = await serviceWithWrongSecret.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(stateBefore, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
        Assert.Equal(0, await db.ProcessedPaymentEvents.CountAsync());
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected_without_touching_any_PaymentIntent()
    {
        var (db, service, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-tampered";
        var intent = SeedIntent(db, PaymentIntentState.Authorized, providerRef);
        var stateBefore = intent.State;

        var json = BuildPayload("txn-tampered", providerRef, "CP", "000.000.000");
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);
        var tamperedBodyHex = (Convert.ToByte(bodyHex[..2], 16) ^ 0xFF).ToString("X2") + bodyHex[2..];

        var result = await service.ProcessAsync(tamperedBodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(stateBefore, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
        Assert.Equal(0, await db.PaymentIntents.CountAsync(p => p.State != stateBefore));
    }

    [Theory]
    [InlineData(null, "aabb")]
    [InlineData("aabb", null)]
    [InlineData("", "")]
    public async Task Missing_headers_are_rejected(string? ivHex, string? tagHex)
    {
        var (db, service, _) = CreateSut();
        SeedIntent(db, PaymentIntentState.Authorized, "/v1/payments/whatever");

        var result = await service.ProcessAsync("deadbeef", ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(0, await db.ProcessedPaymentEvents.CountAsync());
    }

    [Fact]
    public async Task Missing_configured_secret_rejects_everything()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var service = new HyperPayWebhookService(db, BuildConfig(secret: null), NullLogger<HyperPayWebhookService>.Instance);

        var result = await service.ProcessAsync("deadbeef", "aabb", "ccdd");

        Assert.Equal(HyperPayWebhookOutcome.InvalidSignature, result.Outcome);
    }

    // ── Illegal implied transition (defense in depth) ───────────────────

    [Fact]
    public async Task Capture_notification_for_an_already_refunded_intent_is_rejected_not_applied()
    {
        var (db, service, secret) = CreateSut();
        var providerRef = "/v1/payments/txn-alreadyrefunded";
        var intent = SeedIntent(db, PaymentIntentState.Captured, providerRef);
        intent.TransitionTo(PaymentIntentState.Refunded);
        db.SaveChanges();

        // A stale/out-of-order "capture succeeded" notification arriving after the refund.
        var json = BuildPayload("txn-alreadyrefunded", providerRef, "CP", "000.000.000");
        var (bodyHex, ivHex, tagHex) = HyperPayWebhookTestHelper.Encrypt(json, secret);

        var result = await service.ProcessAsync(bodyHex, ivHex, tagHex);

        Assert.Equal(HyperPayWebhookOutcome.TransitionRejected, result.Outcome);
        Assert.Equal(PaymentIntentState.Refunded, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
    }
}
