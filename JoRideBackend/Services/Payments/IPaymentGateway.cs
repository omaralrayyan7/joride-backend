using JoRideBackend.Models.Payments;

namespace JoRideBackend.Services.Payments
{
    /// <summary>Result of preparing a checkout (HyperPay Copy&amp;Pay's "prepare checkout" step).</summary>
    public record PaymentCheckoutResult(string CheckoutId, string? WidgetOrRedirectUrl, string RawResponse);

    /// <summary>Result of a gateway operation that settles money (authorize/capture/void/refund).</summary>
    public record PaymentGatewayResult(bool Success, string? ProviderRef, string? ResultCode, string RawResponse);

    /// <summary>
    /// A payment provider capable of holding, capturing, voiding, and refunding money
    /// against a <see cref="PaymentIntent"/>. Implementations drive the intent's own state
    /// machine (<see cref="PaymentIntent.TransitionTo"/>) as a side effect of each call —
    /// success moves the intent forward, failure moves it to <see cref="PaymentIntentState.Failed"/>,
    /// and calling an operation the intent isn't in the right state for throws before any
    /// network call is made.
    /// </summary>
    public interface IPaymentGateway
    {
        /// <summary>
        /// Starts a checkout for this intent (e.g. HyperPay Copy&amp;Pay's prepare-checkout
        /// step). Does not itself transition intent.State — the payer still has to complete
        /// payment via the returned checkout (widget/redirect) before <see cref="AuthorizeAsync"/>
        /// can confirm it.
        /// </summary>
        Task<PaymentCheckoutResult> CreateCheckoutAsync(PaymentIntent intent, CancellationToken ct = default);

        /// <summary>
        /// Confirms a completed checkout and places a hold for the intent's amount.
        /// On success transitions the intent to Authorized; on failure, to Failed.
        /// </summary>
        Task<PaymentGatewayResult> AuthorizeAsync(PaymentIntent intent, string checkoutId, CancellationToken ct = default);

        /// <summary>
        /// Captures (settles) a previously authorized hold. On success transitions the
        /// intent to Captured; on failure, to Failed. Requires the intent to be Authorized.
        /// </summary>
        Task<PaymentGatewayResult> CaptureAsync(PaymentIntent intent, CancellationToken ct = default);

        /// <summary>
        /// Releases a previously authorized hold without capturing it. On success
        /// transitions the intent to Voided. Requires the intent to be Authorized.
        /// </summary>
        Task<PaymentGatewayResult> VoidAsync(PaymentIntent intent, CancellationToken ct = default);

        /// <summary>
        /// Refunds a captured payment (full or partial amount). On success transitions the
        /// intent to Refunded. Requires the intent to be Captured.
        /// </summary>
        Task<PaymentGatewayResult> RefundAsync(PaymentIntent intent, decimal amount, CancellationToken ct = default);
    }
}
