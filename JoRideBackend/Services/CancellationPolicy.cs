namespace JoRideBackend.Services;

/// <summary>
/// E3.2 fee tiers. NOTE on interpretation: the task describes a classic
/// "free &gt;24h before / partial &lt;24h before / full no-show" policy, which
/// assumes a booking made in advance of a future start time. This codebase's
/// booking model has no such thing — TripsController.Start always sets
/// StartTime = now, so a trip is only created once it has already begun and
/// been paid for; there is no pre-start reservation window and therefore no
/// literal "no-show". The tiers below are reinterpreted against the only
/// future timestamp a trip actually has — ScheduledEndTime — so "cancelling"
/// means ending a still-InProgress rental early/late relative to its booked
/// window: plenty of time left is treated like a clean cancellation (free),
/// cancelling close to or past the scheduled return is treated like the
/// no-show case (partial/full fee), matching the economic intent of the
/// original policy under this architecture.
/// </summary>
public static class CancellationPolicy
{
    public const decimal PartialFeeRate = 0.25m;

    public readonly record struct Result(decimal RefundAmount, decimal FeeAmount, string Tier);

    public static Result Evaluate(DateTime nowUtc, DateTime? scheduledEndUtc, decimal totalFare)
    {
        if (totalFare <= 0) return new Result(0m, 0m, "none");

        // No scheduled end to compare against — treat as immediately cancellable, no fee.
        if (scheduledEndUtc is null) return new Result(totalFare, 0m, "free");

        var hoursRemaining = (scheduledEndUtc.Value - nowUtc).TotalHours;

        if (hoursRemaining > 24)
            return new Result(totalFare, 0m, "free");

        if (hoursRemaining >= 0)
        {
            var fee = Math.Round(totalFare * PartialFeeRate, 2, MidpointRounding.AwayFromZero);
            return new Result(totalFare - fee, fee, "partial");
        }

        // Scheduled window already elapsed without a return — no-show equivalent.
        return new Result(0m, totalFare, "no-show");
    }
}
