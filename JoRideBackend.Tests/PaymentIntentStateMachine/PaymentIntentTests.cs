using JoRideBackend.Models.Payments;

namespace JoRideBackend.Tests.PaymentIntentStateMachine;

public class PaymentIntentTests
{
    private static PaymentIntent NewIntent(PaymentIntentState state = PaymentIntentState.Created) => new()
    {
        Id = Guid.NewGuid(),
        Amount = 25.00m,
        Currency = "USD",
        UserId = 1,
        State = state,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Theory]
    [InlineData(PaymentIntentState.Created, PaymentIntentState.Authorized)]
    [InlineData(PaymentIntentState.Created, PaymentIntentState.Failed)]
    [InlineData(PaymentIntentState.Authorized, PaymentIntentState.Captured)]
    [InlineData(PaymentIntentState.Authorized, PaymentIntentState.Voided)]
    [InlineData(PaymentIntentState.Authorized, PaymentIntentState.Failed)]
    [InlineData(PaymentIntentState.Captured, PaymentIntentState.Refunded)]
    public void TransitionTo_allows_legal_transitions(PaymentIntentState from, PaymentIntentState to)
    {
        var intent = NewIntent(from);

        intent.TransitionTo(to);

        Assert.Equal(to, intent.State);
    }

    [Theory]
    [InlineData(PaymentIntentState.Created, PaymentIntentState.Captured)]
    [InlineData(PaymentIntentState.Created, PaymentIntentState.Voided)]
    [InlineData(PaymentIntentState.Created, PaymentIntentState.Refunded)]
    [InlineData(PaymentIntentState.Authorized, PaymentIntentState.Refunded)] // can't refund money never captured
    [InlineData(PaymentIntentState.Captured, PaymentIntentState.Voided)]
    [InlineData(PaymentIntentState.Captured, PaymentIntentState.Authorized)]
    [InlineData(PaymentIntentState.Voided, PaymentIntentState.Authorized)]   // terminal
    [InlineData(PaymentIntentState.Refunded, PaymentIntentState.Captured)]   // terminal
    [InlineData(PaymentIntentState.Failed, PaymentIntentState.Authorized)]   // terminal
    public void TransitionTo_throws_on_illegal_transitions(PaymentIntentState from, PaymentIntentState to)
    {
        var intent = NewIntent(from);

        Assert.Throws<InvalidOperationException>(() => intent.TransitionTo(to));
    }

    [Fact]
    public void TransitionTo_does_not_mutate_state_when_illegal()
    {
        var intent = NewIntent(PaymentIntentState.Voided);
        var updatedAtBefore = intent.UpdatedAt;

        Assert.Throws<InvalidOperationException>(() => intent.TransitionTo(PaymentIntentState.Captured));

        Assert.Equal(PaymentIntentState.Voided, intent.State);
        Assert.Equal(updatedAtBefore, intent.UpdatedAt);
    }

    [Fact]
    public void TransitionTo_bumps_UpdatedAt_on_success()
    {
        var intent = NewIntent(PaymentIntentState.Created);
        intent.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        var before = intent.UpdatedAt;

        intent.TransitionTo(PaymentIntentState.Authorized);

        Assert.True(intent.UpdatedAt > before);
    }
}
