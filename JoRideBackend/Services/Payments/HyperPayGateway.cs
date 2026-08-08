using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using JoRideBackend.Models.Payments;

namespace JoRideBackend.Services.Payments
{
    /// <summary>
    /// Real HyperPay integration (Copy&amp;Pay checkout flow, built on the OPP platform
    /// HyperPay is white-labeled from — see hyperpay.docs.oppwa.com). Groundwork only: no
    /// live/test HyperPay credentials exist yet, so this can't be exercised end-to-end. It
    /// is structured against the documented API shape so wiring up real credentials later
    /// is a config change, not a rewrite:
    ///
    ///   1. POST {baseUrl}/v1/checkouts             — prepare checkout (paymentType=PA, a hold)
    ///   2. (payer completes payment via the widget/redirect using the checkoutId)
    ///   3. GET  {baseUrl}/v1/checkouts/{id}/payment — check status; response's "resourcePath"
    ///                                                  (e.g. "/v1/payments/{id}") is the handle
    ///                                                  for all follow-up operations
    ///   4. POST {baseUrl}{resourcePath}             — paymentType=CP (capture) / RV (void) / RF (refund)
    ///
    /// Throws if HYPERPAY_ENTITY_ID / HYPERPAY_ACCESS_TOKEN aren't configured — never
    /// silently no-ops, since a silent no-op here would mean "money was never actually
    /// charged" going unnoticed.
    /// </summary>
    public class HyperPayGateway : IPaymentGateway
    {
        private readonly HttpClient _http;
        private readonly ILogger<HyperPayGateway> _logger;
        private readonly string? _entityId;
        private readonly string? _accessToken;
        private readonly string _baseUrl;

        public HyperPayGateway(IHttpClientFactory factory, IConfiguration configuration, ILogger<HyperPayGateway> logger)
        {
            _http = factory.CreateClient("hyperpay");
            _logger = logger;
            _entityId = configuration["HYPERPAY_ENTITY_ID"];
            _accessToken = configuration["HYPERPAY_ACCESS_TOKEN"];
            _baseUrl = (configuration["HYPERPAY_BASE_URL"] ?? "https://eu-test.oppwa.com").TrimEnd('/');
        }

        public async Task<PaymentCheckoutResult> CreateCheckoutAsync(PaymentIntent intent, CancellationToken ct = default)
        {
            EnsureConfigured();

            if (intent.State != PaymentIntentState.Created)
            {
                throw new InvalidOperationException(
                    $"Cannot create a checkout for PaymentIntent {intent.Id}: already in state {intent.State}.");
            }

            var form = new Dictionary<string, string>
            {
                ["entityId"] = _entityId!,
                ["amount"] = FormatAmount(intent.Amount),
                ["currency"] = intent.Currency,
                ["paymentType"] = "PA", // pre-authorization hold; CaptureAsync settles it later
                // Echoed back on every webhook notification for this checkout (including the
                // very first "authorize" one, which arrives before we've ever recorded a
                // ProviderRef) — this is how HyperPayWebhookService correlates a notification
                // back to this intent without depending on a HyperPay id we don't have yet.
                ["merchantTransactionId"] = intent.Id.ToString(),
            };

            var raw = await SendAsync(HttpMethod.Post, $"{_baseUrl}/v1/checkouts", form, ct);

            using var doc = JsonDocument.Parse(raw);
            var checkoutId = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(checkoutId))
            {
                throw new InvalidOperationException($"HyperPay checkout response had no \"id\": {raw}");
            }

            _logger.LogInformation("[HyperPay] Checkout created checkoutId={CheckoutId} intentId={IntentId}", checkoutId, intent.Id);
            return new PaymentCheckoutResult(checkoutId, WidgetOrRedirectUrl: null, raw);
        }

        public async Task<PaymentGatewayResult> AuthorizeAsync(PaymentIntent intent, string checkoutId, CancellationToken ct = default)
        {
            EnsureConfigured();
            intent.EnsureCanTransitionTo(PaymentIntentState.Authorized);

            var url = $"{_baseUrl}/v1/checkouts/{Uri.EscapeDataString(checkoutId)}/payment" +
                      $"?entityId={Uri.EscapeDataString(_entityId!)}";
            var raw = await SendAsync(HttpMethod.Get, url, form: null, ct);

            var (success, resultCode, resourcePath) = ParseResult(raw);

            intent.TransitionTo(success ? PaymentIntentState.Authorized : PaymentIntentState.Failed);
            if (!string.IsNullOrEmpty(resourcePath))
            {
                intent.ProviderRef = resourcePath;
            }

            _logger.LogInformation(
                "[HyperPay] Authorize intentId={IntentId} checkoutId={CheckoutId} resultCode={ResultCode} success={Success}",
                intent.Id, checkoutId, resultCode, success);
            return new PaymentGatewayResult(success, intent.ProviderRef, resultCode, raw);
        }

        public Task<PaymentGatewayResult> CaptureAsync(PaymentIntent intent, CancellationToken ct = default) =>
            SendBackOfficeOperationAsync(intent, "CP", intent.Amount, PaymentIntentState.Captured, ct);

        public Task<PaymentGatewayResult> VoidAsync(PaymentIntent intent, CancellationToken ct = default) =>
            SendBackOfficeOperationAsync(intent, "RV", intent.Amount, PaymentIntentState.Voided, ct);

        public Task<PaymentGatewayResult> RefundAsync(PaymentIntent intent, decimal amount, CancellationToken ct = default) =>
            SendBackOfficeOperationAsync(intent, "RF", amount, PaymentIntentState.Refunded, ct);

        private async Task<PaymentGatewayResult> SendBackOfficeOperationAsync(
            PaymentIntent intent, string paymentType, decimal amount, PaymentIntentState onSuccess, CancellationToken ct)
        {
            EnsureConfigured();
            // Validated before any network call: an illegal transition throws immediately
            // rather than us sending a real CP/RV/RF against a HyperPay transaction that
            // shouldn't exist yet (e.g. capturing an intent that was never authorized).
            intent.EnsureCanTransitionTo(onSuccess);

            if (string.IsNullOrWhiteSpace(intent.ProviderRef))
            {
                throw new InvalidOperationException(
                    $"Cannot send '{paymentType}' for PaymentIntent {intent.Id}: no ProviderRef " +
                    "(HyperPay transaction resourcePath) recorded — was it ever authorized?");
            }

            var form = new Dictionary<string, string>
            {
                ["entityId"] = _entityId!,
                ["amount"] = FormatAmount(amount),
                ["currency"] = intent.Currency,
                ["paymentType"] = paymentType,
            };

            var raw = await SendAsync(HttpMethod.Post, $"{_baseUrl}{intent.ProviderRef}", form, ct);
            var (success, resultCode, _) = ParseResult(raw);

            intent.TransitionTo(success ? onSuccess : PaymentIntentState.Failed);

            _logger.LogInformation(
                "[HyperPay] {PaymentType} intentId={IntentId} resultCode={ResultCode} success={Success}",
                paymentType, intent.Id, resultCode, success);
            return new PaymentGatewayResult(success, intent.ProviderRef, resultCode, raw);
        }

        private async Task<string> SendAsync(HttpMethod method, string url, Dictionary<string, string>? form, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            var response = await _http.SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }

        private static (bool Success, string ResultCode, string? ResourcePath) ParseResult(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var resultCode = root.TryGetProperty("result", out var result) && result.TryGetProperty("code", out var code)
                ? code.GetString() ?? ""
                : "";
            var resourcePath = root.TryGetProperty("resourcePath", out var rp) ? rp.GetString() : null;
            return (HyperPayResultCodes.IsSuccess(resultCode), resultCode, resourcePath);
        }

        private static string FormatAmount(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

        private void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(_entityId) || string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new InvalidOperationException(
                    "HyperPay not configured: HYPERPAY_ENTITY_ID and HYPERPAY_ACCESS_TOKEN must both be set.");
            }
        }
    }
}
