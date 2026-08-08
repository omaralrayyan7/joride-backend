using System.ComponentModel.DataAnnotations;
using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Controllers
{
    public record CommandNoteRequest(
        int VehicleId,
        [Required, StringLength(20)] string CommandType,
        [Required, StringLength(1000, MinimumLength = 1)] string Reason);

    /// <summary>
    /// E7: read-only/note-taking endpoints that exist purely to feed the new admin
    /// dashboard (Views/Admin). Deliberately a SEPARATE, brand-new controller rather than
    /// added actions on KycAdminController/DeviceCommandsController/AdminPaymentsController/
    /// TripsController — those are left completely untouched per the task's constraint.
    /// Nothing here writes money, changes a PaymentIntent/DeviceCommand/PendingTopUp's
    /// state, or touches a vehicle's lock — it only reads existing data (device
    /// telemetry, pending top-ups) or records a supplementary audit note.
    /// </summary>
    [ApiController]
    [Route("api/admin/dashboard")]
    [Authorize(Policy = "AdminOnly")]
    public class AdminDashboardController : ControllerBase
    {
        // A GPS tracker reporting less often than this (with nothing more recent on
        // record) is considered offline. Traccar devices in practice report every
        // 10-60s; 5 minutes gives generous room for a missed poll cycle or two
        // (TraccarPollingService itself polls every 10s) without flagging a merely-slow
        // device as down.
        private static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(5);

        private readonly PaymentsDbContext _db;

        public AdminDashboardController(PaymentsDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Device health per vehicle, from TelemetrySnapshot — the Postgres-persisted output
        /// of TraccarService's REST polling (TraccarPollingService, E1.1/E1.4), not a fresh
        /// live call. Persisted "last known" is the more robust source for a dashboard: it
        /// still reports a meaningful (if stale) status when Traccar itself is temporarily
        /// unreachable, rather than the page going blank.
        /// </summary>
        [HttpGet("device-health")]
        public async Task<ActionResult<IEnumerable<object>>> GetDeviceHealth(CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var latestByVehicle = await _db.TelemetrySnapshots
                .Where(t => t.VehicleId != null)
                .GroupBy(t => t.VehicleId)
                .Select(g => g.OrderByDescending(t => t.DeviceTime).First())
                .ToListAsync(ct);

            var latestByVehicleId = latestByVehicle.ToDictionary(t => t.VehicleId!.Value);

            var result = VehiclesController.AllVehicles().Select(v =>
            {
                latestByVehicleId.TryGetValue(v.Id, out var snapshot);
                var lastSeen = snapshot?.DeviceTime;
                var online = lastSeen.HasValue && now - lastSeen.Value <= OfflineThreshold;

                return new
                {
                    vehicleId = v.Id,
                    licensePlate = v.LicensePlate,
                    model = v.Model,
                    lastPositionTime = lastSeen,
                    online,
                    minutesSinceLastReport = lastSeen.HasValue ? (int)Math.Round((now - lastSeen.Value).TotalMinutes) : (int?)null,
                };
            });

            return Ok(result);
        }

        /// <summary>Lists PendingTopUp rows still awaiting admin reconciliation — no such
        /// list endpoint exists on AdminPaymentsController today, only confirm/reject-by-id.</summary>
        [HttpGet("topups/pending")]
        public async Task<ActionResult<IEnumerable<PendingTopUp>>> GetPendingTopUps(CancellationToken ct)
        {
            var pending = await _db.PendingTopUps
                .Where(t => t.Status == PendingTopUpStatus.Pending)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync(ct);

            return Ok(pending);
        }

        /// <summary>
        /// Records the admin's stated reason for a device command as a general audit note.
        /// This is supplementary only: DeviceCommandsController's request has no "reason"
        /// field (its POST /api/vehicles/{id}/commands/{type} takes no body at all), and
        /// that controller is off-limits to modify, so the dashboard's command console calls
        /// THIS endpoint first (the UI makes the reason a required field before either call
        /// fires) and then calls the real, unmodified, safety-gated DeviceCommandsController
        /// endpoint for the actual dispatch — the reason is never used to authorize or
        /// influence that call in any way, it is purely a parallel audit trail entry.
        /// </summary>
        [HttpPost("command-notes")]
        public IActionResult RecordCommandNote([FromBody] CommandNoteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest("Reason is required.");

            var adminId = User.FindFirst("sub")?.Value ?? "0";
            var adminName = User.FindFirst("name")?.Value ?? "admin";

            AuditController.Log("DeviceCommandReasonNoted", "Vehicle", request.VehicleId,
                $"Admin: {adminName} (#{adminId})", "Admin",
                $"Reason given for {request.CommandType}: {request.Reason}");

            return Ok(new { recorded = true });
        }
    }
}
