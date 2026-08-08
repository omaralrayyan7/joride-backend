using JoRideBackend.Models.Payments;
using JoRideBackend.Tests.Fakes;

namespace JoRideBackend.Tests.PaymentIntentStateMachine;

/// <summary>
/// Exercises the full PaymentIntent lifecycle exactly as a real IPaymentGateway caller
/// would, using FakePaymentGateway. Because FakePaymentGateway drives the transitions
/// through PaymentIntent.TransitionTo (not by setting .State directly), these tests
/// exercise the same enforcement a real gateway call would hit.
/// </summary>
public class FakePaymentGatewayWorkflowTests
{
    private static PaymentIntent NewIntent() => new()
    {
        Id = Guid.NewGuid(),
        Amount = 40.00m,
        Currency = "USD",
        UserId = 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Full_lifecycle_checkout_authorize_capture_refund()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent();
        Assert.Equal(PaymentIntentState.Created, intent.State);

        var checkout = await gateway.CreateCheckoutAsync(intent);
        Assert.False(string.IsNullOrEmpty(checkout.CheckoutId));
        Assert.Equal(PaymentIntentState.Created, intent.State); // checkout creation doesn't transition

        var authResult = await gateway.AuthorizeAsync(intent, checkout.CheckoutId);
        Assert.True(authResult.Success);
        Assert.Equal(PaymentIntentState.Authorized, intent.State);
        Assert.False(string.IsNullOrEmpty(intent.ProviderRef));

        var captureResult = await gateway.CaptureAsync(intent);
        Assert.True(captureResult.Success);
        Assert.Equal(PaymentIntentState.Captured, intent.State);

        var refundResult = await gateway.RefundAsync(intent, intent.Amount);
        Assert.True(refundResult.Success);
        Assert.Equal(PaymentIntentState.Refunded, intent.State);
    }

    [Fact]
    public async Task Authorize_then_void_releases_the_hold()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent();

        var checkout = await gateway.CreateCheckoutAsync(intent);
        await gateway.AuthorizeAsync(intent, checkout.CheckoutId);

        var voidResult = await gateway.VoidAsync(intent);

        Assert.True(voidResult.Success);
        Assert.Equal(PaymentIntentState.Voided, intent.State);
    }

    [Fact]
    public async Task CreateCheckout_throws_if_intent_is_not_Created()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent();
        await gateway.AuthorizeAsync(intent, "checkout-1"); // now Authorized

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CreateCheckoutAsync(intent));
    }

    [Fact]
    public async Task Capturing_a_never_authorized_intent_throws_and_never_reaches_Captured()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent(); // still Created — never authorized

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CaptureAsync(intent));

        Assert.Equal(PaymentIntentState.Created, intent.State);
    }

    [Fact]
    public async Task Refunding_an_authorized_but_uncaptured_intent_throws()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent();
        await gateway.AuthorizeAsync(intent, "checkout-1");

        // Authorized (a hold) has no captured money to reverse — refund must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(intent, intent.Amount));

        Assert.Equal(PaymentIntentState.Authorized, intent.State);
    }

    [Fact]
    public async Task Voiding_an_already_captured_intent_throws()
    {
        var gateway = new FakePaymentGateway();
        var intent = NewIntent();
        await gateway.AuthorizeAsync(intent, "checkout-1");
        await gateway.CaptureAsync(intent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.VoidAsync(intent));

        Assert.Equal(PaymentIntentState.Captured, intent.State);
    }
}
