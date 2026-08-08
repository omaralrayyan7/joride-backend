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
        /// Captures (settles) a previously authorized hold, in full or in part — HyperPay's
        /// real CP operation genuinely supports capturing less than the held amount, and even
        /// multiple partial captures against the same hold (confirmed against OPPWA's docs).
        /// <paramref name="amount"/> defaults to the full held amount. On success transitions
        /// the intent to Captured (and, for a partial capture, updates intent.Amount to the
        /// amount actually captured — see PaymentAdminService.PartialCaptureAsync for why);
        /// on failure, to Failed. Requires the intent to be Authorized.
        /// </summary>
        Task<PaymentGatewayResult> CaptureAsync(PaymentIntent intent, decimal? amount = null, CancellationToken ct = default);

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

        /// <summary>
        /// Releases the remaining un-captured amount of a hold that has already been
        /// partially captured — real HyperPay operation (RV against the same resourcePath),
        /// used only by the partial-capture flow. Deliberately does NOT call
        /// PaymentIntent.TransitionTo: the intent's own lifecycle already correctly concluded
        /// at Captured (a PaymentIntent has exactly one terminal outcome — Captured or
        /// Voided, never both), so this only needs to tell HyperPay to stop holding the rest
        /// and let the caller record the matching ledger entry. See
        /// PaymentAdminService.PartialCaptureAsync for the full design rationale.
        /// </summary>
        Task<PaymentGatewayResult> ReleaseRemainingHoldAsync(PaymentIntent intent, decimal remainingAmount, CancellationToken ct = default);
    }
}
