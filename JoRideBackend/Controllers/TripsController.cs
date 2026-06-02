using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class TripsController : ControllerBase
{
    static readonly List<Trip> trips = new();
    static int _nextId = 1;
    internal static FirestoreService? _firestore;
    internal static TraccarService? _traccar;

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

    [HttpPost("start")]
    public async Task<ActionResult<Trip>> Start(StartTripRequest request)
    {
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

        var paid = await WalletController.TryChargeAsync(
            request.UserId,
            request.TotalFare,
            $"Trip payment for vehicle #{request.VehicleId}",
            request.PaymentMethod);

        if (!paid)
        {
            return BadRequest("Payment failed or wallet balance is not enough. Digital key was not issued.");
        }

        var trip = new Trip
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
            TotalFare = request.TotalFare,
            PaymentMethod = request.PaymentMethod,
            PaymentStatus = "Paid",
            PaidAt = now,
            DigitalKeyEnabled = true,
            Status = "InProgress",
        };

        trips.Add(trip);
        VehiclesController.SetStatus(trip.VehicleId, "InUse");
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

        NotificationsController.Push(
            request.UserId,
            "Booking Confirmed",
            $"Payment confirmed. Your digital key for vehicle #{request.VehicleId} is active until {scheduledEnd:yyyy-MM-dd HH:mm} UTC.",
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

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, Trip update)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();

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

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        var trip = trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();
        trips.Remove(trip);
        return NoContent();
    }
}
