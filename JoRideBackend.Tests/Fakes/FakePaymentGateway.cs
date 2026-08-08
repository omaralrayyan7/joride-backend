using JoRideBackend.Models.Payments;
using JoRideBackend.Services.Payments;

namespace JoRideBackend.Tests.Fakes;

/// <summary>
/// Test-only IPaymentGateway. Lives exclusively in this test project — JoRideBackend
/// (the shipped app) has no project or package reference to JoRideBackend.Tests at all,
/// so this type does not exist in the production assembly and cannot be resolved by
/// Program.cs's DI container under any environment. See
/// TestPaymentGatewayRegistration for the additional explicit "Testing" environment
/// guard used when this needs to be wired through a real DI container (e.g. a future
/// WebApplicationFactory-based integration test).
///
/// Simulates a well-behaved gateway: every operation "succeeds" and drives the
/// PaymentIntent's own state machine exactly like a real gateway would — it does not
/// bypass PaymentIntent.TransitionTo, so illegal-transition tests exercise the same
/// enforcement a real gateway call would hit.
/// </summary>
public class FakePaymentGateway : IPaymentGateway
{
    private int _checkoutCounter;

    public Task<PaymentCheckoutResult> CreateCheckoutAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        if (intent.State != PaymentIntentState.Created)
        {
            throw new InvalidOperationException(
                $"Cannot create a checkout for PaymentIntent {intent.Id}: already in state {intent.State}.");
        }

        var checkoutId = $"fake-checkout-{++_checkoutCounter}";
        return Task.FromResult(new PaymentCheckoutResult(checkoutId, WidgetOrRedirectUrl: null, RawResponse: "{}"));
    }

    public Task<PaymentGatewayResult> AuthorizeAsync(PaymentIntent intent, string checkoutId, CancellationToken ct = default)
    {
        intent.TransitionTo(PaymentIntentState.Authorized);
        intent.ProviderRef = $"fake-txn-{intent.Id:N}";
        return Task.FromResult(new PaymentGatewayResult(true, intent.ProviderRef, "000.000.000", "{}"));
    }

    public Task<PaymentGatewayResult> CaptureAsync(PaymentIntent intent, decimal? amount = null, CancellationToken ct = default)
    {
        var captureAmount = amount ?? intent.Amount;
        if (captureAmount <= 0 || captureAmount > intent.Amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), captureAmount,
                $"Capture amount must be > 0 and <= the held amount ({intent.Amount}).");
        }

        intent.Amount = captureAmount;
        intent.TransitionTo(PaymentIntentState.Captured);
        return Task.FromResult(new PaymentGatewayResult(true, intent.ProviderRef, "000.000.000", "{}"));
    }

    public Task<PaymentGatewayResult> VoidAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        intent.TransitionTo(PaymentIntentState.Voided);
        return Task.FromResult(new PaymentGatewayResult(true, intent.ProviderRef, "000.000.000", "{}"));
    }

    public Task<PaymentGatewayResult> RefundAsync(PaymentIntent intent, decimal amount, CancellationToken ct = default)
    {
        intent.TransitionTo(PaymentIntentState.Refunded);
        return Task.FromResult(new PaymentGatewayResult(true, intent.ProviderRef, "000.000.000", "{}"));
    }

    public Task<PaymentGatewayResult> ReleaseRemainingHoldAsync(PaymentIntent intent, decimal remainingAmount, CancellationToken ct = default)
    {
        if (remainingAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingAmount), remainingAmount, "Remaining amount must be positive.");
        }

        return Task.FromResult(new PaymentGatewayResult(true, intent.ProviderRef, "000.000.000", "{}"));
    }
}
