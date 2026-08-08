using JoRideBackend.Services.Payments;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Receives HyperPay payment-status notifications.
///
/// MECHANISM — verified, not assumed: HyperPay is white-labeled on the OPPWA platform
/// (its own docs live at hyperpay.docs.oppwa.com), and the shared OPPWA webhook mechanism
/// is documented identically across every OPPWA-based provider checked (Peach Payments,
/// SIBS Gateway, Hobex) at developer.peachpayments.com/docs/oppwa-guides-webhooks. This
/// confirms two things:
///
///   1. It IS genuine webhook push, not merchant-side polling. OPPWA POSTs to a URL you
///      configure once (alongside a webhook "Secret") whenever a payment's status changes
///      — the /v1/checkouts/{id}/payment status-*poll* HyperPayGateway.AuthorizeAsync uses
///      is a separate, complementary mechanism (checking status right after the payer
///      completes the widget), not a substitute for this endpoint.
///
///   2. Unlike most PSPs, OPPWA does NOT sign the payload with an HMAC over the raw body.
///      The entire JSON payload is AES-256-GCM ENCRYPTED with a 64-hex-char (256-bit) key
///      — the same secret configured as HYPERPAY_WEBHOOK_SECRET. The initialization
///      vector and authentication tag arrive as separate hex-encoded headers
///      (X-Initialization-Vector, X-Authentication-Tag); the request body itself is the
///      hex-encoded ciphertext. Successfully AES-GCM-decrypting and authenticating the
///      body with the shared secret IS the verification — there's no separate signature
///      to compare against. A wrong secret, tampered ciphertext, or corrupted headers all
///      surface as a decryption/authentication failure, which we treat as an
///      invalid/unverifiable event (401) without ever parsing or acting on the body.
///
/// See HyperPayWebhookService for the decrypt -> idempotency -> PaymentIntent.TransitionTo
/// pipeline (kept out of this controller so it's testable without an HTTP host).
/// </summary>
[ApiController]
[Route("api/payments/webhooks")]
public class HyperPayWebhookController : ControllerBase
{
    private const string InitializationVectorHeader = "X-Initialization-Vector";
    private const string AuthenticationTagHeader = "X-Authentication-Tag";

    private readonly HyperPayWebhookService _webhooks;

    public HyperPayWebhookController(HyperPayWebhookService webhooks)
    {
        _webhooks = webhooks;
    }

    [HttpPost("hyperpay")]
    public async Task<IActionResult> ReceiveWebhook(CancellationToken ct)
    {
        var ivHex = Request.Headers[InitializationVectorHeader].ToString();
        var tagHex = Request.Headers[AuthenticationTagHeader].ToString();

        using var reader = new StreamReader(Request.Body);
        var bodyHex = await reader.ReadToEndAsync(ct);

        var result = await _webhooks.ProcessAsync(bodyHex, ivHex, tagHex, ct);

        return result.Outcome switch
        {
            HyperPayWebhookOutcome.InvalidSignature => StatusCode(StatusCodes.Status401Unauthorized),
            HyperPayWebhookOutcome.MalformedPayload => BadRequest(),
            // Accepted, DuplicateIgnored, IntentNotFound, TransitionRejected: all safe 200s —
            // none of them are something OPPWA retrying the delivery would fix.
            _ => Ok(),
        };
    }
}
