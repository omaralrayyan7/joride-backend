using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Payments
{
    public enum HyperPayWebhookOutcome
    {
        /// <summary>New, valid event: PaymentIntent was found and transitioned (or the
        /// transition was legitimately rejected — see TransitionRejected).</summary>
        Accepted,

        /// <summary>Already-processed event (same provider reference) — safe no-op.</summary>
        DuplicateIgnored,

        /// <summary>Decryption/authentication failed, or the secret isn't configured.</summary>
        InvalidSignature,

        /// <summary>Decrypted fine but the JSON didn't have a usable payment payload.</summary>
        MalformedPayload,

        /// <summary>Valid, new event, but no PaymentIntent has this provider reference.</summary>
        IntentNotFound,

        /// <summary>Valid, new event, but the implied transition is illegal for the intent's
        /// current state (e.g. a redelivered "captured" notification after a refund already
        /// happened) — PaymentIntent.TransitionTo threw, exactly as it should.</summary>
        TransitionRejected,
    }

    public record HyperPayWebhookProcessResult(HyperPayWebhookOutcome Outcome, string Detail);

    /// <summary>
    /// Decrypts, verifies, and applies HyperPay webhook notifications. See
    /// HyperPayWebhookController for the documented mechanism (AES-256-GCM, not HMAC).
    /// Split out from the controller so it's testable without an HTTP host.
    /// </summary>
    public class HyperPayWebhookService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly PaymentsDbContext _db;
        private readonly IConfiguration _configuration;
        private readonly ILogger<HyperPayWebhookService> _logger;

        public HyperPayWebhookService(PaymentsDbContext db, IConfiguration configuration, ILogger<HyperPayWebhookService> logger)
        {
            _db = db;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<HyperPayWebhookProcessResult> ProcessAsync(
            string bodyHex, string? ivHex, string? tagHex, CancellationToken ct = default)
        {
            var secretHex = _configuration["HYPERPAY_WEBHOOK_SECRET"];
            if (string.IsNullOrWhiteSpace(secretHex))
            {
                _logger.LogError("[HyperPayWebhook] HYPERPAY_WEBHOOK_SECRET not configured — rejecting all webhooks.");
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.InvalidSignature, "Webhook secret not configured.");
            }

            if (string.IsNullOrWhiteSpace(ivHex) || string.IsNullOrWhiteSpace(tagHex) || string.IsNullOrWhiteSpace(bodyHex))
            {
                _logger.LogWarning("[HyperPayWebhook] Rejected: missing IV/auth-tag/body.");
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.InvalidSignature, "Missing IV, auth tag, or body.");
            }

            string plaintext;
            try
            {
                plaintext = Decrypt(bodyHex, ivHex, tagHex, secretHex);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Wrong secret, tampered ciphertext, corrupted/malformed hex, or a bad tag —
                // AES-GCM authentication failing IS the "invalid signature" case here.
                _logger.LogWarning(ex, "[HyperPayWebhook] Rejected: failed to decrypt/authenticate payload.");
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.InvalidSignature, "Decryption/authentication failed.");
            }

            HyperPayWebhookEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<HyperPayWebhookEnvelope>(plaintext, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[HyperPayWebhook] Rejected: malformed JSON after decryption.");
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.MalformedPayload, "Malformed JSON.");
            }

            var payload = envelope?.Payload;
            if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
            {
                _logger.LogWarning("[HyperPayWebhook] Rejected: decrypted body had no usable payment payload.");
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.MalformedPayload, "No payment payload/id.");
            }

            var providerEventId = string.IsNullOrWhiteSpace(payload.ResourcePath)
                ? $"/v1/payments/{payload.Id}"
                : payload.ResourcePath!;

            // Idempotency check happens before any PaymentIntent is touched.
            var alreadyProcessed = await _db.ProcessedPaymentEvents
                .AnyAsync(e => e.ProviderEventId == providerEventId, ct);
            if (alreadyProcessed)
            {
                _logger.LogInformation("[HyperPayWebhook] Duplicate event {ProviderEventId} — no-op.", providerEventId);
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.DuplicateIgnored, providerEventId);
            }

            // Correlate primarily via merchantTransactionId (our own PaymentIntent.Id, set on
            // checkout creation and echoed back on every event for it) — this is the only way
            // to match the very first "authorize" notification, which arrives before we've
            // ever recorded a ProviderRef. Fall back to ProviderRef for older/partial payloads.
            PaymentIntent? intent = null;
            if (Guid.TryParse(payload.MerchantTransactionId, out var merchantIntentId))
            {
                intent = await _db.PaymentIntents.FindAsync(new object[] { merchantIntentId }, ct);
            }
            intent ??= await _db.PaymentIntents.FirstOrDefaultAsync(p => p.ProviderRef == providerEventId, ct);

            // Recorded regardless of what happens below, so a redelivery of an unmatched or
            // rejected event is *also* a no-op next time rather than repeating the same work.
            _db.ProcessedPaymentEvents.Add(new ProcessedPaymentEvent
            {
                Id = Guid.NewGuid(),
                ProviderEventId = providerEventId,
                ProcessedAt = DateTime.UtcNow,
            });

            if (intent is null)
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning("[HyperPayWebhook] No PaymentIntent found for {ProviderEventId}.", providerEventId);
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.IntentNotFound, providerEventId);
            }

            var success = HyperPayResultCodes.IsSuccess(payload.Result?.Code);
            var targetState = ResolveTargetState(payload.PaymentType, success);

            try
            {
                // Never bypassed: this is the same TransitionTo every gateway call goes
                // through, so an illegal implied transition (e.g. a stale/out-of-order
                // redelivery) throws instead of silently corrupting intent.State.
                intent.TransitionTo(targetState);
                if (string.IsNullOrWhiteSpace(intent.ProviderRef))
                {
                    // First event we've seen for this intent (matched via merchantTransactionId,
                    // since there was no ProviderRef yet) — record it so CaptureAsync/VoidAsync/
                    // RefundAsync have a HyperPay transaction to reference afterward.
                    intent.ProviderRef = providerEventId;
                }
            }
            catch (InvalidOperationException ex)
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning(ex,
                    "[HyperPayWebhook] Rejected transition for intentId={IntentId} state={State} -> {TargetState}.",
                    intent.Id, intent.State, targetState);
                return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.TransitionRejected, providerEventId);
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "[HyperPayWebhook] intentId={IntentId} paymentType={PaymentType} resultCode={ResultCode} -> {State}",
                intent.Id, payload.PaymentType, payload.Result?.Code, intent.State);

            return new HyperPayWebhookProcessResult(HyperPayWebhookOutcome.Accepted, providerEventId);
        }

        private static PaymentIntentState ResolveTargetState(string? paymentType, bool success)
        {
            if (!success)
                return PaymentIntentState.Failed;

            return paymentType switch
            {
                "PA" => PaymentIntentState.Authorized,
                "DB" => PaymentIntentState.Captured, // debit = authorize+capture in one step
                "CP" => PaymentIntentState.Captured,
                "RV" => PaymentIntentState.Voided,
                "RF" => PaymentIntentState.Refunded,
                _ => PaymentIntentState.Failed, // unrecognized paymentType — fail closed, don't guess
            };
        }

        private static string Decrypt(string bodyHex, string ivHex, string tagHex, string secretHex)
        {
            var key = Convert.FromHexString(secretHex);
            var iv = Convert.FromHexString(ivHex);
            var tag = Convert.FromHexString(tagHex);
            var ciphertext = Convert.FromHexString(bodyHex);

            var plaintext = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(key, tag.Length);
            aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
    }
}
