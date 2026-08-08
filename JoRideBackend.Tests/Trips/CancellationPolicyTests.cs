using JoRideBackend.Services;

namespace JoRideBackend.Tests.Trips;

public class CancellationPolicyTests
{
    [Fact]
    public void More_than_24h_of_the_booked_window_remaining_is_a_free_cancellation()
    {
        var now = DateTime.UtcNow;
        var scheduledEnd = now.AddHours(25);

        var result = CancellationPolicy.Evaluate(now, scheduledEnd, 100m);

        Assert.Equal("free", result.Tier);
        Assert.Equal(100m, result.RefundAmount);
        Assert.Equal(0m, result.FeeAmount);
    }

    [Fact]
    public void Within_24h_of_the_scheduled_end_charges_a_partial_fee()
    {
        var now = DateTime.UtcNow;
        var scheduledEnd = now.AddHours(10);

        var result = CancellationPolicy.Evaluate(now, scheduledEnd, 100m);

        Assert.Equal("partial", result.Tier);
        Assert.Equal(25m, result.FeeAmount);
        Assert.Equal(75m, result.RefundAmount);
        Assert.Equal(100m, result.RefundAmount + result.FeeAmount);
    }

    [Fact]
    public void Past_the_scheduled_end_is_a_full_no_show_charge_with_no_refund()
    {
        var now = DateTime.UtcNow;
        var scheduledEnd = now.AddHours(-2);

        var result = CancellationPolicy.Evaluate(now, scheduledEnd, 100m);

        Assert.Equal("no-show", result.Tier);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(100m, result.FeeAmount);
    }

    [Fact]
    public void Refund_plus_fee_always_equals_the_total_fare()
    {
        var now = DateTime.UtcNow;

        foreach (var hours in new[] { -100, -1, 0, 1, 23.99, 24, 24.01, 100 })
        {
            var result = CancellationPolicy.Evaluate(now, now.AddHours(hours), 137.42m);
            Assert.Equal(137.42m, result.RefundAmount + result.FeeAmount);
        }
    }

    [Fact]
    public void Zero_fare_trip_yields_no_refund_and_no_fee()
    {
        var result = CancellationPolicy.Evaluate(DateTime.UtcNow, DateTime.UtcNow.AddHours(48), 0m);

        Assert.Equal("none", result.Tier);
        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(0m, result.FeeAmount);
    }
}
