using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/digitalkey")]
public class DigitalKeyController : ControllerBase
{
    static readonly Dictionary<int, bool> _keyStates = new();

    private readonly TraccarService _traccar;

    public DigitalKeyController(TraccarService traccar)
    {
        _traccar = traccar;
    }

    [HttpGet("status/{tripId:int}")]
    public IActionResult GetStatus(int tripId)
    {
        var trip = TripsController.GetTrip(tripId);
        if (trip is null) return NotFound("Trip not found.");

        var hasAccess = TripsController.HasActivePaidTrip(tripId);
        var isLocked = _keyStates.TryGetValue(tripId, out var state) ? state : true;

        return Ok(new
        {
            tripId,
            hasAccess,
            isLocked,
            paymentStatus = trip.PaymentStatus,
            keyEnabled = trip.DigitalKeyEnabled,
            scheduledEndTime = trip.ScheduledEndTime,
            message = hasAccess ? "Digital key active." : "Digital key unavailable until payment is confirmed and trip is active."
        });
    }

    [HttpPost("unlock")]
    public IActionResult Unlock([FromBody] KeyRequest request)
    {
        if (!TripsController.HasActivePaidTrip(request.TripId))
            return BadRequest("Digital key is not active. Payment must be confirmed and trip must be in progress.");

        _keyStates[request.TripId] = false;

        // ── Notify Traccar: vehicle unlocked ──────────────────────────────────
        var trip = TripsController.GetTrip(request.TripId);
        var vehicle = trip is not null ? VehiclesController.GetVehicleById(trip.VehicleId) : null;
        if (vehicle is not null)
        {
            _ = _traccar.SendUnlockEventAsync(
                deviceId: vehicle.LicensePlate ?? $"vehicle-{vehicle.Id}",
                lat:      vehicle.Latitude,
                lon:      vehicle.Longitude,
                tripId:   request.TripId);
        }

        LogKeyAction("VehicleUnlocked", trip, vehicle);
        return Ok(new { tripId = request.TripId, isLocked = false, hasAccess = true });
    }

    [HttpPost("lock")]
    public IActionResult Lock([FromBody] KeyRequest request)
    {
        if (!TripsController.HasActivePaidTrip(request.TripId))
            return BadRequest("Digital key is not active. Payment must be confirmed and trip must be in progress.");

        _keyStates[request.TripId] = true;

        // ── Notify Traccar: vehicle locked ────────────────────────────────────
        var trip = TripsController.GetTrip(request.TripId);
        var vehicle = trip is not null ? VehiclesController.GetVehicleById(trip.VehicleId) : null;
        if (vehicle is not null)
        {
            _ = _traccar.SendLockEventAsync(
                deviceId: vehicle.LicensePlate ?? $"vehicle-{vehicle.Id}",
                lat:      vehicle.Latitude,
                lon:      vehicle.Longitude,
                tripId:   request.TripId);
        }

        LogKeyAction("VehicleLocked", trip, vehicle);
        return Ok(new { tripId = request.TripId, isLocked = true, hasAccess = true });
    }

    private static void LogKeyAction(string action, JoRideBackend.Models.Trip? trip, JoRideBackend.Models.Vehicle? vehicle)
    {
        var user = trip is not null ? UsersController.GetUser(trip.UserId) : null;
        AuditController.Log(
            action,
            "Vehicle",
            vehicle?.Id ?? 0,
            $"User: {user?.Name ?? "?"} (#{trip?.UserId})",
            "User",
            $"Trip #{trip?.Id}, vehicle {vehicle?.LicensePlate}");
    }
}
