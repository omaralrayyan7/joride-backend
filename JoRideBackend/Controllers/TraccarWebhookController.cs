using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Receives events Traccar forwards in near-real-time (event.forward.url) —
/// ignition on/off, motion, online/offline — as an alternative to polling
/// positions. See docs/ops/traccar-event-forwarding.md for how to configure
/// Traccar and why authentication works the way it does here.
/// </summary>
[ApiController]
[Route("api/traccar")]
public class TraccarWebhookController : ControllerBase
{
    private const string SignatureHeader = "X-Traccar-Signature";
    private const string SecretQueryParam = "secret";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<TraccarWebhookController> _logger;
    private readonly IConfiguration _configuration;

    public TraccarWebhookController(ILogger<TraccarWebhookController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    [HttpPost("events")]
    public async Task<IActionResult> ReceiveEvent()
    {
        var secret = _configuration["TRACCAR_WEBHOOK_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError("[TraccarWebhook] TRACCAR_WEBHOOK_SECRET is not configured — rejecting all events.");
            return StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Read the raw body ourselves (rather than [FromBody]) so we can verify
        // the signature against the exact bytes Traccar sent before parsing anything.
        using var bodyReader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await bodyReader.ReadToEndAsync();
        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);

        if (!IsAuthorized(bodyBytes, secret))
        {
            _logger.LogWarning("[TraccarWebhook] Rejected event: missing or invalid signature.");
            return StatusCode(StatusCodes.Status401Unauthorized);
        }

        TraccarEventWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TraccarEventWebhookPayload>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[TraccarWebhook] Rejected event: malformed JSON body.");
            return BadRequest();
        }

        var evt = payload?.Event;
        if (evt is null)
        {
            _logger.LogWarning("[TraccarWebhook] Rejected event: body had no \"event\" object.");
            return BadRequest();
        }

        LogDomainEvent(evt, payload!.Position);

        return Ok();
    }

    // ── Authentication ───────────────────────────────────────────────────
    //
    // Preferred: a real HMAC-SHA256 hex digest of the raw body, keyed with
    // TRACCAR_WEBHOOK_SECRET — this is what we implement and test here, and
    // what any capable client (our tests, a proxy in front of Traccar) should send.
    //
    // Traccar itself cannot do this: its built-in forwarder (event.forward.header)
    // only supports a static, hand-configured header value — it cannot compute a
    // per-request signature over a body it hasn't hashed anywhere. So we also
    // accept the shared secret sent verbatim, either as the X-Traccar-Signature
    // header value or as a `secret` query-string param on event.forward.url —
    // both are things stock Traccar can actually do. See docs/ops/ for details.
    private bool IsAuthorized(byte[] bodyBytes, string secret)
    {
        var signatureHeader = Request.Headers[SignatureHeader].ToString();
        if (!string.IsNullOrEmpty(signatureHeader))
        {
            if (IsValidHmacSignature(bodyBytes, signatureHeader, secret))
                return true;

            if (SecretEquals(signatureHeader, secret))
                return true;
        }

        if (Request.Query.TryGetValue(SecretQueryParam, out var querySecret) &&
            SecretEquals(querySecret.ToString(), secret))
        {
            return true;
        }

        return false;
    }

    /// <summary>Case-insensitive, constant-time hex digest comparison.</summary>
    private static bool IsValidHmacSignature(byte[] bodyBytes, string providedSignature, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computedHex = Convert.ToHexString(hmac.ComputeHash(bodyBytes)); // uppercase hex
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature.ToUpperInvariant());
        var computedBytes = Encoding.UTF8.GetBytes(computedHex);
        return CryptographicOperations.FixedTimeEquals(providedBytes, computedBytes);
    }

    /// <summary>Exact, case-sensitive, constant-time comparison against the raw shared secret.</summary>
    private static bool SecretEquals(string provided, string secret) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(secret));

    // ── Event mapping (log-only for now — persistence is E1.4) ───────────

    private void LogDomainEvent(TraccarEvent evt, TraccarPosition? position)
    {
        var location = position is not null
            ? $" lat={position.Latitude} lon={position.Longitude}"
            : string.Empty;

        switch (evt.Type)
        {
            case "ignitionOn":
                _logger.LogInformation(
                    "[TraccarWebhook] Ignition ON for deviceId={DeviceId} at {EventTime:o}{Location}",
                    evt.DeviceId, evt.EventTime, location);
                break;
            case "ignitionOff":
                _logger.LogInformation(
                    "[TraccarWebhook] Ignition OFF for deviceId={DeviceId} at {EventTime:o}{Location}",
                    evt.DeviceId, evt.EventTime, location);
                break;
            case "deviceOnline":
                _logger.LogInformation(
                    "[TraccarWebhook] Device ONLINE for deviceId={DeviceId} at {EventTime:o}",
                    evt.DeviceId, evt.EventTime);
                break;
            case "deviceOffline":
                _logger.LogInformation(
                    "[TraccarWebhook] Device OFFLINE for deviceId={DeviceId} at {EventTime:o}",
                    evt.DeviceId, evt.EventTime);
                break;
            case "deviceMoving":
                _logger.LogInformation(
                    "[TraccarWebhook] Device MOVING for deviceId={DeviceId} at {EventTime:o}{Location}",
                    evt.DeviceId, evt.EventTime, location);
                break;
            case "deviceStopped":
                _logger.LogInformation(
                    "[TraccarWebhook] Device STOPPED for deviceId={DeviceId} at {EventTime:o}{Location}",
                    evt.DeviceId, evt.EventTime, location);
                break;
            default:
                _logger.LogInformation(
                    "[TraccarWebhook] Unhandled event type={Type} deviceId={DeviceId} at {EventTime:o}",
                    evt.Type, evt.DeviceId, evt.EventTime);
                break;
        }
    }
}
