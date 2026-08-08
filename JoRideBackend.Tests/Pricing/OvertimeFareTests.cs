using JoRideBackend.Models;
using JoRideBackend.Services;

namespace JoRideBackend.Tests.Pricing;

/// <summary>
/// E4.1: PricingController.CalculateOvertimeFare decomposes overtime into whole-days +
/// whole-hours + remaining-minutes, each billed at its own rate ("graduated"/tax-bracket
/// style), instead of picking one tier for the entire overtime duration. Economy rates used
/// throughout (seeded below): 0.15/min, 8/hour, 45/day — matching PricingController.Seed().
/// </summary>
public class OvertimeFareTests
{
    public OvertimeFareTests()
    {
        var firestore = new FirestoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance);

        JoRideBackend.Models.Pricing Rate(string category, decimal min, decimal hr, decimal day) => new()
        {
            Category = category, MinuteRate = min, HourlyRate = hr, DailyRate = day, IsActive = true,
        };

        PricingController.Initialize(new List<JoRideBackend.Models.Pricing>
        {
            Rate("Economy", 0.15m, 8m, 45m),
        }, firestore);
    }

    [Fact]
    public void On_time_or_early_return_has_no_overtime_fare()
    {
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(-5);

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(0, result.billedMinutes);
        Assert.Equal(0m, result.fare);
        Assert.Equal("none", result.rateApplied);
    }

    [Fact]
    public void Very_short_overtime_is_billed_per_minute()
    {
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(3);

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(3, result.billedMinutes);
        Assert.Equal(0.45m, result.fare); // 3 * 0.15
        Assert.Equal("min", result.rateApplied);
    }

    [Fact]
    public void Exact_hour_overtime_is_billed_at_the_flat_hourly_rate()
    {
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(60);

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(60, result.billedMinutes);
        Assert.Equal(8m, result.fare); // 1 * HourlyRate, not 60 * MinuteRate (9.00)
        Assert.Equal("hour", result.rateApplied);
    }

    [Fact]
    public void Overtime_within_the_hourly_tier_blends_hours_and_minutes()
    {
        // 90 minutes over = 1h30m. The old single-tier logic rounded this up to a flat
        // 2 hours (16.00); graduated billing charges 1 hour + 30 minutes.
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(90);

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(90, result.billedMinutes);
        Assert.Equal(12.5m, result.fare); // 1*8 + 30*0.15
        Assert.Equal("hour", result.rateApplied);
    }

    [Fact]
    public void Overtime_crossing_from_the_hourly_into_the_daily_tier_blends_correctly()
    {
        // 1450 minutes over = 1 day + 10 minutes. The old logic rounded this up to a flat
        // 2 days (90.00); graduated billing charges 1 day + 10 minutes.
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(1450);

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(1450, result.billedMinutes);
        Assert.Equal(46.5m, result.fare); // 1*45 + 10*0.15
        Assert.Equal("day", result.rateApplied);
    }

    [Fact]
    public void Two_full_days_of_overtime_bills_two_days_flat()
    {
        // Exactly on a tier boundary — no leftover hours/minutes to blend in.
        var scheduledEnd = DateTime.UtcNow;
        var actualEnd = scheduledEnd.AddMinutes(2880); // 2 * 1440

        var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, actualEnd);

        Assert.Equal(90m, result.fare); // 2 * 45, not 3 days from over-rounding
        Assert.Equal("day", result.rateApplied);
    }

    [Fact]
    public void Long_overtime_never_costs_more_than_pure_per_minute_billing()
    {
        // Graduated (day+hour+minute) billing is a bulk discount over paying MinuteRate for
        // every minute — this is the invariant the old single-tier-for-the-whole-duration
        // logic violated at large spans (e.g. 24h10m rounded up to a flat 2 days = 90.00,
        // versus the correct 1 day + 10 min = 46.50; both are less than the 217.50 a pure
        // per-minute rate would charge, but 90.00 overshoots what graduated billing should
        // ever produce for 1450 minutes).
        var scheduledEnd = DateTime.UtcNow;

        foreach (var minutes in new[] { 3, 90, 700, 1450, 2880, 4321 })
        {
            var result = PricingController.CalculateOvertimeFare("Economy", scheduledEnd, scheduledEnd.AddMinutes(minutes));
            var pureMinuteCost = minutes * 0.15m;
            Assert.True(result.fare <= pureMinuteCost,
                $"At {minutes} minutes, graduated fare {result.fare} exceeded pure per-minute cost {pureMinuteCost}");
        }
    }
}
