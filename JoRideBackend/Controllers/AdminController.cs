using System.Security.Claims;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly TraccarService _traccar;

    public AdminController(TraccarService traccar)
    {
        _traccar = traccar;
    }

    // ── Dashboard statistics ───────────────────────────────────────────────────
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var vehicles = VehiclesController.AllVehicles();
        var trips    = TripsController.AllTrips();

        return Ok(new
        {
            totalUsers      = UsersController.AllUsers().Count,
            totalCars       = vehicles.Count,
            availableCars   = vehicles.Count(v => v.Status == "Available"),
            inUseCars       = vehicles.Count(v => v.Status == "InUse"),
            tripsInProgress = trips.Count(t => t.Status == "InProgress"),
            totalTrips      = trips.Count,
        });
    }

    // ── Telematics / remote car control ────────────────────────────────────────
    [HttpPost("vehicles/{id:int}/unlock")]
    public IActionResult Unlock(int id) => RemoteAction(id, "unlock");

    [HttpPost("vehicles/{id:int}/lock")]
    public IActionResult Lock(int id) => RemoteAction(id, "lock");

    [HttpPost("vehicles/{id:int}/engine/start")]
    public IActionResult StartEngine(int id) => RemoteAction(id, "engine_start");

    [HttpPost("vehicles/{id:int}/engine/kill")]
    public IActionResult KillEngine(int id) => RemoteAction(id, "engine_kill");

    private IActionResult RemoteAction(int vehicleId, string command)
    {
        var vehicle = VehiclesController.GetVehicleById(vehicleId);
        if (vehicle is null) return NotFound("Vehicle not found.");

        var deviceId = vehicle.LicensePlate ?? $"vehicle-{vehicle.Id}";
        var (actor, role) = GetActor();

        string action, message;
        switch (command)
        {
            case "unlock":
                _ = _traccar.SendAdminLockEventAsync(deviceId, vehicle.Latitude, vehicle.Longitude, locked: false);
                action = "VehicleUnlocked"; message = "Vehicle unlocked.";
                break;
            case "lock":
                _ = _traccar.SendAdminLockEventAsync(deviceId, vehicle.Latitude, vehicle.Longitude, locked: true);
                action = "VehicleLocked"; message = "Vehicle locked.";
                break;
            case "engine_start":
                _ = _traccar.SendEngineEventAsync(deviceId, vehicle.Latitude, vehicle.Longitude, engineOn: true);
                action = "EngineStarted"; message = "Engine started.";
                break;
            case "engine_kill":
                _ = _traccar.SendEngineEventAsync(deviceId, vehicle.Latitude, vehicle.Longitude, engineOn: false);
                action = "EngineKilled"; message = "Engine killed.";
                break;
            default:
                return BadRequest("Unknown command.");
        }

        AuditController.Log(action, "Vehicle", vehicle.Id, actor, role,
            $"Remote '{command}' on {deviceId}.");

        return Ok(new { vehicleId = vehicle.Id, command, message });
    }

    private (string actor, string role) GetActor()
    {
        var id   = User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub")
                   ?? "?";
        var name = User.FindFirstValue(ClaimTypes.Name)
                   ?? User.FindFirstValue("name")
                   ?? "admin";
        return ($"Admin: {name} (#{id})", "Admin");
    }
}
