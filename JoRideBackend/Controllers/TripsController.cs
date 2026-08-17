using System.Collections.Concurrent;
using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class TripsController : ControllerBase
{
    static readonly List<Trip> trips = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;
    internal static TraccarService? _traccar;

    // ── E3.1: per-vehicle async lock ────────────────────────────────────────
    // Trips are Firestore-backed, but the actual synchronous source of truth
    // during request handling is this process' in-memory `trips` list — two
    // concurrent ASP.NET Core requests both read/mutate that same list before
    // either one's Firestore write lands. A Firestore transaction (the task's
    // literal wording) would not protect against that; it would only protect
    // against two separate PROCESSES racing on the Firestore document, which
    // isn't the actual race here. The correct guard is an in-process lock
    // around the "check overlap, then reserve" critical section, scoped per
    // vehicle so unrelated vehicles' bookings don't serialize against each
    // other. C#'s `lock` can't wrap an `await`, so this uses SemaphoreSlim —
    // the same pattern AuditController's static list already uses `lock` for,
    // adapted for async.
    static readonly ConcurrentDictionary<int, SemaphoreSlim> _vehicleLocks = new();
    static SemaphoreSlim VehicleLock(int vehicleId) => _vehicleLocks.GetOrAdd(vehicleId, _ => new SemaphoreSlim(1, 1));

    static bool HasOverlap(int vehicleId, DateTime windowStart, DateTime windowEnd, int? excludeTripId = null) =>
        trips.Any(t =>
            t.VehicleId == vehicleId &&
            t.Id != excludeTripId &&
            t.Status == "InProgress" &&
            t.StartTime < windowEnd &&
            windowStart < (t.ScheduledEndTime ?? DateTime.MaxValue));

    public static void Initialize(List<Trip> loaded, FirestoreService fs)
    {
        trips.Clear();
        trips.AddRange(loaded);
        _nextId = loaded.Count > 0 ? loaded.Max(t => t.Id) + 1 : 1;
        _firestore = fs;
    }

    public static Trip? GetTrip(int id) => trips.FirstOrDefault(t => t.Id == id);
    public static IReadOnlyList<Trip> AllTrips() => trips;

    public static bool HasActivePaidTrip(int tripId)
    {
        var trip = GetTrip(tripId);
        return trip is not null
            && trip.Status == "InProgress"
            && trip.PaymentStatus == "Paid"
            && trip.DigitalKeyEnabled;
    }

    [HttpGet]
    public ActionResult<IEnumerable<Trip>> GetAll([FromQuery] int? userId)
    {
        var result = userId.HasValue ? trips.Where(t => t.UserId == userId.Value) : trips;
        return result.OrderByDescending(t => t.StartTime).ToList();
    }

    [HttpGet("active")]
    public ActionResult<Trip> GetActive([FromQuery] int userId)
    {
        var trip = trips
            .Where(t => t.UserId == userId && t.Status == "InProgress" && t.PaymentStatus == "Paid")
            .OrderByDescending(t => t.StartTime)
            .FirstOrDefault();
        return trip is null ? NotFound("No active paid trip found.") : trip;
    }

    [HttpGet("{id:int}")]
    public ActionResult<Trip> Get(int id)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        return trip is null ? NotFound() : trip;
    }

    // E4.3: admin-only visibility into overdue trips so ops can decide whether to intervene.
    // Computed live against the same threshold OverdueTripMonitorService uses, rather than
    // just reading the OverdueFlaggedAt cache, so this is accurate even between poll ticks.
    // This endpoint only reports — it never calls DeviceCommandService itself; any
    // immobilize/lock action stays a deliberate, separate admin call to that controller.
    [Authorize]
    [HttpGet("overdue")]
    public ActionResult<IEnumerable<object>> GetOverdue()
    {
        if (!User.HasClaim("role", "admin")) return Forbid();

        var now = DateTime.UtcNow;
        var overdue = trips
            .Where(t => t.Status == "InProgress"
                     && t.EndTime is null
                     && t.ScheduledEndTime.HasValue
                     && now > t.ScheduledEndTime.Value.ToUniversalTime() + OverdueTripMonitorService.GracePeriod)
            .OrderByDescending(t => t.ScheduledEndTime)
            .Select(t => new
            {
                t.Id,
                t.UserId,
                t.VehicleId,
                t.ScheduledEndTime,
                minutesOverdue = (int)Math.Round((now - t.ScheduledEndTime!.Value.ToUniversalTime()).TotalMinutes),
                t.OverdueFlaggedAt,
                notified = t.OverdueFlaggedAt is not null,
            });

        return Ok(overdue);
    }

    // Booking gate (E2.2): only KYC-Approved, authenticated users may start a trip. This
    // checks the CALLER's own "kycStatus" claim (see JwtTokenService/KycApprovedRequirement).
    [Authorize(Policy = "KycApproved")]
    [HttpPost("start")]
    public async Task<ActionResult<Trip>> Start(StartTripRequest request)
    {
        // Ownership check: a caller may only book for themselves unless they're an admin —
        // otherwise any authenticated, KYC-approved user could charge an arbitrary other
        // user's wallet just by putting a different UserId in the body.
        var callerId = User.FindFirst("sub")?.Value;
        var isAdmin = User.HasClaim("role", "admin");
        if (!isAdmin && (callerId is null || !int.TryParse(callerId, out var callerIdInt) || callerIdInt != request.UserId))
        {
            return Forbid();
        }

        if (!UsersController.Exists(request.UserId)) return BadRequest("User not found");

        // ── Debt gate: block booking while the wallet balance is negative ──────
        var bookingUser = UsersController.GetUser(request.UserId);
        if (bookingUser is not null && bookingUser.WalletBalance < 0)
            return BadRequest($"You have an outstanding balance of {Math.Abs(bookingUser.WalletBalance):F2} JOD. Please top up your wallet to clear the debt before booking a new trip.");

        if (!VehiclesController.Exists(request.VehicleId)) return BadRequest("Vehicle not found");
        if (!VehiclesController.IsAvailable(request.VehicleId)) return BadRequest("Vehicle is not available for booking.");
        if (request.Duration <= 0) return BadRequest("Duration must be greater than zero.");
        if (!new[] { "min", "hour", "day" }.Contains(request.DurationType)) return BadRequest("Invalid duration type.");
        if (request.TotalFare <= 0) return BadRequest("Total fare must be greater than zero.");

        var active = trips.Any(t => t.UserId == request.UserId && t.Status == "InProgress");
        if (active) return BadRequest("User already has an active trip.");

        var now = DateTime.UtcNow;
        var scheduledEnd = request.DurationType switch
        {
            "min" => now.AddMinutes(request.Duration),
            "hour" => now.AddHours(request.Duration),
            "day" => now.AddDays(request.Duration),
            _ => now
        };

        // ── Loyalty discount (computed before the lock — read-only) ────────────
        var (_, discountPct, _) = LoyaltyController.ComputeTier(request.UserId);
        var discountAmount = discountPct > 0
            ? Math.Round(request.TotalFare * discountPct / 100m, 2)
            : 0m;
        var chargeAmount = request.TotalFare - discountAmount;

        // ── E3.1: atomic overlap check + reservation ────────────────────────
        // Everything between acquiring the per-vehicle lock and adding `trip`
        // to the list (or bailing out) is the critical section: it re-checks
        // the overlap definitively (the earlier VehiclesController.IsAvailable
        // check above is only a fast-path/UX check — it's not race-safe on its
        // own) and reserves the slot by inserting the Trip row while still
        // holding the lock, so a second concurrent request for the same
        // vehicle can only acquire the lock AFTER this one's reservation is
        // already visible in `trips`.
        Trip trip;
        var vehicleLock = VehicleLock(request.VehicleId);
        await vehicleLock.WaitAsync();
        try
        {
            if (HasOverlap(request.VehicleId, now, scheduledEnd))
            {
                return Conflict("Vehicle unavailable: it is already booked for an overlapping time window.");
            }

            trip = new Trip
            {
                Id = _nextId++,
                UserId = request.UserId,
                VehicleId = request.VehicleId,
                StartTime = now,
                ScheduledEndTime = scheduledEnd,
                EndTime = null,
                Duration = request.Duration,
                DurationType = request.DurationType,
                BaseFare = request.BaseFare,
                BookingFee = request.BookingFee,
                Tax = request.Tax,
                TotalFare = chargeAmount,
                DiscountPercent = discountPct,
                DiscountAmount = discountAmount,
                PaymentMethod = request.PaymentMethod,
                PaymentStatus = "Pending",
                PaidAt = null,
                DigitalKeyEnabled = true,
                Status = "InProgress",
            };

            trips.Add(trip);
            VehiclesController.SetStatus(trip.VehicleId, "InUse");
        }
        finally
        {
            vehicleLock.Release();
        }

        // Payment happens outside the lock — it doesn't affect the overlap
        // invariant, and slower external gateways shouldn't hold the vehicle
        // lock. If it fails, roll back the reservation under the same lock.
        var paid = await WalletController.TryChargeAsync(
            request.UserId,
            chargeAmount,
            $"Trip payment for vehicle #{request.VehicleId}",
            request.PaymentMethod);

        if (!paid)
        {
            await vehicleLock.WaitAsync();
            try
            {
                trips.Remove(trip);
                VehiclesController.SetStatus(trip.VehicleId, "Available");
            }
            finally
            {
                vehicleLock.Release();
            }

            return BadRequest("Payment failed or wallet balance is not enough. Digital key was not issued.");
        }

        trip.PaymentStatus = "Paid";
        trip.PaidAt = now;
        await (_firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);

        // ── Notify Traccar: vehicle booked ────────────────────────────────────
        var bookedVehicle = VehiclesController.GetVehicleById(trip.VehicleId);
        if (_traccar is not null && bookedVehicle is not null)
        {
            _ = _traccar.SendBookingEventAsync(
                deviceId: bookedVehicle.LicensePlate ?? $"vehicle-{trip.VehicleId}",
                lat:      bookedVehicle.Latitude,
                lon:      bookedVehicle.Longitude,
                tripId:   trip.Id,
                userId:   trip.UserId);
        }

        var discountNote = discountAmount > 0
            ? $" ({discountPct}% loyalty discount applied, saved {discountAmount:F2} JOD)"
            : string.Empty;
        NotificationsController.Push(
            request.UserId,
            "Booking Confirmed",
            $"Payment confirmed{discountNote}. Your digital key for vehicle #{request.VehicleId} is active until {scheduledEnd:yyyy-MM-dd HH:mm} UTC.",
            "booking");

        var bookingUserName = UsersController.GetUser(trip.UserId)?.Name ?? "?";
        AuditController.Log("TripBooked", "Trip", trip.Id,
            $"User: {bookingUserName} (#{trip.UserId})", "User",
            $"Vehicle #{trip.VehicleId} ({bookedVehicle?.LicensePlate}) for {request.Duration} {request.DurationType}, {request.TotalFare:F2} JOD.");

        return CreatedAtAction(nameof(Get), new { id = trip.Id }, trip);
    }

    [HttpPut("{id:int}/end")]
    public async Task<ActionResult<Trip>> End(int id, EndTripRequest request)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();
        if (trip.EndTime is not null) return BadRequest("Trip already completed");

        var actualEndUtc = request.EndTime.ToUniversalTime();
        trip.EndTime = actualEndUtc;

        var vehicle = VehiclesController.GetVehicleById(trip.VehicleId);
        var scheduledEndUtc = trip.ScheduledEndTime?.ToUniversalTime();
        if (scheduledEndUtc.HasValue)
        {
            var overtime = PricingController.CalculateOvertimeFare(vehicle?.Category, scheduledEndUtc.Value, actualEndUtc);
            trip.OvertimeMinutes = overtime.billedMinutes;
            trip.OvertimeFare = overtime.fare;

            if (overtime.fare > 0)
            {
                // Overtime is unavoidable: allow the wallet to go negative (debt) so the
                // charge always lands. The user is then blocked from new bookings until
                // the debt is cleared (see Start).
                var charged = await WalletController.TryChargeAsync(
                    trip.UserId,
                    overtime.fare,
                    $"Overtime charge for trip #{trip.Id} ({overtime.billedMinutes} minutes, billed as {overtime.rateApplied})",
                    trip.PaymentMethod ?? "joRide Wallet",
                    allowNegative: true);

                trip.OvertimePaymentStatus = charged ? "Paid" : "Pending";
                if (charged) trip.TotalFare += overtime.fare;
            }
            else
            {
                trip.OvertimePaymentStatus = "None";
            }
        }

        // ── Early-return refund ───────────────────────────────────────────────
        // If the user returned the car before the scheduled end time, calculate
        // the unused portion and refund it to their JoWallet as credit.
        if (scheduledEndUtc.HasValue && actualEndUtc < scheduledEndUtc.Value && trip.OvertimeMinutes == 0)
        {
            var usedDuration = actualEndUtc - trip.StartTime.ToUniversalTime();
            var rates        = PricingController.GetRatesForCategory(vehicle?.Category);
            decimal refundAmount = 0m;
            string  refundNote   = string.Empty;

            switch ((trip.DurationType ?? "min").ToLowerInvariant())
            {
                case "min":
                {
                    var usedMin = Math.Max(1, (int)Math.Ceiling(usedDuration.TotalMinutes));
                    var cost    = usedMin * rates.MinuteRate;
                    refundAmount = trip.BaseFare - cost;
                    refundNote   = $"{usedMin} min used (of {trip.Duration} booked)";
                    break;
                }
                case "hour":
                {
                    var usedHr = Math.Max(1, (int)Math.Ceiling(usedDuration.TotalHours));
                    var cost   = usedHr * rates.HourlyRate;
                    refundAmount = trip.BaseFare - cost;
                    refundNote   = $"{usedHr}h used (of {trip.Duration}h booked)";
                    break;
                }
                case "day":
                {
                    var usedDay = Math.Max(1, (int)Math.Ceiling(usedDuration.TotalDays));
                    var cost    = usedDay * rates.DailyRate;
                    refundAmount = trip.BaseFare - cost;
                    refundNote   = $"{usedDay}d used (of {trip.Duration}d booked)";
                    break;
                }
            }

            if (refundAmount > 0)
            {
                trip.TotalFare -= refundAmount;
                await WalletController.RefundAsync(
                    trip.UserId,
                    refundAmount,
                    $"Early-return refund for trip #{trip.Id} — {refundNote}");

                NotificationsController.Push(
                    trip.UserId,
                    "Early Return Refund",
                    $"{refundAmount:F2} JOD refunded to your JoWallet. {refundNote}.",
                    "payment");
            }
        }
        // ─────────────────────────────────────────────────────────────────────

        trip.Status = "Completed";
        trip.DigitalKeyEnabled = false;
        VehiclesController.SetStatus(trip.VehicleId, "Available");

        await (_firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);

        // ── Notify Traccar: trip ended, vehicle available ─────────────────────
        var endedVehicle = VehiclesController.GetVehicleById(trip.VehicleId);
        if (_traccar is not null && endedVehicle is not null)
        {
            _ = _traccar.SendTripEndedEventAsync(
                deviceId: endedVehicle.LicensePlate ?? $"vehicle-{trip.VehicleId}",
                lat:      endedVehicle.Latitude,
                lon:      endedVehicle.Longitude,
                tripId:   trip.Id);
        }

        var overtimeText = trip.OvertimeFare > 0
            ? $" Overtime: {trip.OvertimeMinutes} minute(s), {trip.OvertimeFare:F2} JOD ({trip.OvertimePaymentStatus})."
            : string.Empty;

        NotificationsController.Push(
            trip.UserId,
            "Trip Completed",
            $"Your trip ended. Final amount: {trip.TotalFare:F2} JOD.{overtimeText}",
            "payment");

        var endUserName = UsersController.GetUser(trip.UserId)?.Name ?? "?";
        AuditController.Log("TripEnded", "Trip", trip.Id,
            $"User: {endUserName} (#{trip.UserId})", "User",
            $"Vehicle #{trip.VehicleId} ({endedVehicle?.LicensePlate}). Final {trip.TotalFare:F2} JOD.{overtimeText}");

        return trip;
    }

    // ── E3.2: cancellation with policy-tiered fee ───────────────────────────
    // State machine: only a still-InProgress, not-yet-ended trip is
    // cancellable. Trips are already fully paid at booking time (see Start),
    // so "cancel" here means: end the rental now, then refund whatever the
    // policy tier says is owed back — the unrefunded remainder IS the fee.
    // That fee needs no separate ledger entry: it's already sitting in
    // revenue:payments from the original charge (see WalletController.
    // TryChargeAsync) and is left untouched, exactly like the early-return
    // refund logic in End() above. Writing an extra "fee" entry for the same
    // money would double-count revenue that was already recognized at
    // booking. See CancellationPolicy.cs for the tier interpretation.
    [Authorize]
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<Trip>> Cancel(int id, [FromBody] CancelTripRequest? request)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();

        var callerId = User.FindFirst("sub")?.Value;
        var isAdmin = User.HasClaim("role", "admin");
        if (!isAdmin && (callerId is null || !int.TryParse(callerId, out var callerIdInt) || callerIdInt != trip.UserId))
        {
            return Forbid();
        }

        if (trip.Status != "InProgress" || trip.EndTime is not null)
        {
            return BadRequest($"Trip cannot be cancelled: it is already {(trip.Status == "InProgress" ? "completed" : trip.Status?.ToLowerInvariant())}.");
        }

        var now = DateTime.UtcNow;
        var outcome = CancellationPolicy.Evaluate(now, trip.ScheduledEndTime?.ToUniversalTime(), trip.TotalFare);

        trip.EndTime = now;
        trip.Status = "Cancelled";
        trip.DigitalKeyEnabled = false;
        VehiclesController.SetStatus(trip.VehicleId, "Available");

        if (outcome.RefundAmount > 0)
        {
            trip.TotalFare -= outcome.RefundAmount;
            await WalletController.RefundAsync(
                trip.UserId,
                outcome.RefundAmount,
                $"Cancellation refund for trip #{trip.Id} ({outcome.Tier} tier, reason: {request?.Reason ?? "unspecified"})");
        }

        await (_firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);

        NotificationsController.Push(
            trip.UserId,
            "Trip Cancelled",
            outcome.FeeAmount > 0
                ? $"Your trip was cancelled. A {outcome.FeeAmount:F2} JOD cancellation fee applies ({outcome.Tier} tier); {outcome.RefundAmount:F2} JOD refunded."
                : "Your trip was cancelled. Full amount refunded.",
            "booking");

        var cancelUserName = UsersController.GetUser(trip.UserId)?.Name ?? "?";
        AuditController.Log("TripCancelled", "Trip", trip.Id,
            $"User: {cancelUserName} (#{trip.UserId})", isAdmin ? "Admin" : "User",
            $"Tier: {outcome.Tier}. Refunded {outcome.RefundAmount:F2} JOD, fee retained {outcome.FeeAmount:F2} JOD.");

        return trip;
    }

    [Authorize]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Trip update)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();

        var callerId = User.FindFirst("sub")?.Value;
        var isAdmin = User.HasClaim("role", "admin");
        if (!isAdmin && (callerId is null || !int.TryParse(callerId, out var callerIdInt) || callerIdInt != trip.UserId))
        {
            return Forbid();
        }

        trip.UserId = update.UserId;
        trip.VehicleId = update.VehicleId;
        trip.StartTime = update.StartTime == default ? trip.StartTime : update.StartTime;
        trip.ScheduledEndTime = update.ScheduledEndTime;
        trip.EndTime = update.EndTime;
        trip.Duration = update.Duration;
        trip.DurationType = update.DurationType;
        trip.BaseFare = update.BaseFare;
        trip.BookingFee = update.BookingFee;
        trip.Tax = update.Tax;
        trip.TotalFare = update.TotalFare;
        trip.PaymentMethod = update.PaymentMethod;
        trip.PaymentStatus = update.PaymentStatus;
        trip.PaidAt = update.PaidAt;
        trip.DigitalKeyEnabled = update.DigitalKeyEnabled;
        trip.Status = update.Status;

        await (_firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);
        return NoContent();
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();
        trips.Remove(trip);
        return NoContent();
    }
}
