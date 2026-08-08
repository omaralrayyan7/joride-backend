using System.Text.Json;
using JoRideBackend.Data;
using JoRideBackend.Models.Payments;

namespace JoRideBackend.Services
{
    /// <summary>
    /// Security-critical: this is the only path allowed to actuate a vehicle
    /// (unlock/lock/immobilize/mobilize). Every call to <see cref="RequestCommandAsync"/>
    /// writes exactly one DeviceCommand row and exactly one CommandAudit row, no matter
    /// which branch it takes (unauthorized, safety-blocked, dispatch failure, success) —
    /// there is no return path that skips the audit write.
    /// </summary>
    public class DeviceCommandService
    {
        // "No recent position" per the E1.3 spec: our OsmAnd-protocol test device has no
        // ignition signal, so freshness of the last known position is the only proxy we
        // have for "is anyone currently able to observe this vehicle move".
        private static readonly TimeSpan StalePositionThreshold = TimeSpan.FromMinutes(2);

        private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly PaymentsDbContext _db;
        private readonly TraccarService _traccar;
        private readonly ILogger<DeviceCommandService> _logger;

        public DeviceCommandService(PaymentsDbContext db, TraccarService traccar, ILogger<DeviceCommandService> logger)
        {
            _db = db;
            _traccar = traccar;
            _logger = logger;
        }

        public async Task<DeviceCommand> RequestCommandAsync(
            int vehicleId,
            DeviceCommandType type,
            int requestingUserId,
            bool isAdmin,
            CancellationToken ct = default)
        {
            var command = new DeviceCommand
            {
                Id = Guid.NewGuid(),
                VehicleId = vehicleId,
                Type = type,
                State = DeviceCommandState.Queued,
                RequestedByUserId = requestingUserId,
                RequestedAt = DateTime.UtcNow,
                ImeiOrDeviceId = string.Empty,
            };
            _db.DeviceCommands.Add(command);
            await _db.SaveChangesAsync(ct);

            // Authorization is enforced here, not only via [Authorize] on the controller,
            // specifically so a rejected attempt still gets an immutable audit row.
            if (!isAdmin)
            {
                await ResolveAsync(command, DeviceCommandState.Unauthorized,
                    "Rejected: caller is not an admin.", positionSnapshotJson: null, ct);
                return command;
            }

            var vehicle = VehiclesController.GetVehicleById(vehicleId);
            if (vehicle is null || string.IsNullOrWhiteSpace(vehicle.LicensePlate))
            {
                await ResolveAsync(command, DeviceCommandState.Failed,
                    "Rejected: vehicle not found or has no registered device identifier.", null, ct);
                return command;
            }

            command.ImeiOrDeviceId = vehicle.LicensePlate;

            TraccarDevice? device = null;
            TraccarPosition? position = null;
            try
            {
                device = await _traccar.FindDeviceByUniqueIdAsync(vehicle.LicensePlate, ct);
                if (device is not null)
                {
                    position = await _traccar.GetPositionAsync(device.Id, ct);
                }
            }
            catch (Exception ex)
            {
                // Fail closed: a lookup error leaves `position` null, which the safety gate
                // below treats as "no recent position" for Immobilize — never guess safe.
                _logger.LogWarning(ex,
                    "[DeviceCommand] Failed to look up Traccar device/position for vehicleId={VehicleId}", vehicleId);
            }

            var positionSnapshotJson = position is null ? null : JsonSerializer.Serialize(position, SnapshotJsonOptions);

            if (type == DeviceCommandType.Immobilize)
            {
                var violation = GetImmobilizeSafetyViolation(position);
                if (violation is not null)
                {
                    await ResolveAsync(command, DeviceCommandState.SafetyBlocked,
                        $"SafetyBlocked: {violation}", positionSnapshotJson, ct);
                    return command;
                }
            }

            if (device is null)
            {
                await ResolveAsync(command, DeviceCommandState.Failed,
                    "Rejected: no matching device registered in Traccar for this vehicle.", positionSnapshotJson, ct);
                return command;
            }

            command.State = DeviceCommandState.Sent;
            await _db.SaveChangesAsync(ct);

            var (accepted, summary) = await DispatchWithRetryAsync(device.Id, type, ct);

            // Confirmation TODO (real E1 future work): once real hardware exists, "Confirmed"
            // should instead watch for the expected telemetry change (e.g. ignition/lock
            // attribute flipping) rather than trusting Traccar's HTTP accept of the command.
            // Our OsmAnd test device can't execute commands at all, so for now "did Traccar's
            // command API accept it" is the only signal we have.
            var finalState = accepted ? DeviceCommandState.Confirmed : DeviceCommandState.Failed;
            await ResolveAsync(command, finalState, summary, positionSnapshotJson, ct);
            return command;
        }

        public async Task<DeviceCommand?> GetCommandAsync(Guid commandId, CancellationToken ct = default) =>
            await _db.DeviceCommands.FindAsync(new object?[] { commandId }, ct);

        private static string? GetImmobilizeSafetyViolation(TraccarPosition? position)
        {
            if (position is null)
                return "no known position for this device.";

            var age = DateTime.UtcNow - position.DeviceTime.ToUniversalTime();
            if (age > StalePositionThreshold)
                return $"latest position is {age.TotalMinutes:F1} min old (must be within {StalePositionThreshold.TotalMinutes:F0} min).";

            if (Math.Abs(position.Speed) > 0.01)
                return $"vehicle is moving (speed={position.Speed}).";

            return null;
        }

        /// <summary>
        /// Not the full telemetry-watch retry logic (that needs real hardware) — just a
        /// single retry if the Traccar call itself times out or errors, per the E1.3 spec.
        /// </summary>
        private async Task<(bool Accepted, string Summary)> DispatchWithRetryAsync(
            long deviceId, DeviceCommandType type, CancellationToken ct)
        {
            var (traccarType, attributes) = MapToTraccarCommand(type);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var result = await _traccar.SendCommandAsync(deviceId, traccarType, attributes, ct);
                    return result.Accepted
                        ? (true, $"Traccar accepted command '{traccarType}' (HTTP {result.StatusCode}).")
                        : (false, $"Traccar rejected command '{traccarType}' (HTTP {result.StatusCode}): {Truncate(result.ResponseBody, 200)}");
                }
                catch (Exception ex) when (attempt == 1)
                {
                    _logger.LogWarning(ex,
                        "[DeviceCommand] Traccar command dispatch errored (attempt {Attempt}/2), retrying once.", attempt);
                }
                catch (Exception ex)
                {
                    return (false, $"Traccar call errored after retry: {Truncate(ex.Message, 200)}");
                }
            }

            return (false, "Traccar call failed after retry.");
        }

        /// <summary>
        /// Traccar has no dedicated door lock/unlock command type (confirmed against its
        /// command model) — outputControl (relay index 1) is the conventional mapping used
        /// for lock/unlock across GPS-tracker integrations. Immobilize/Mobilize map to
        /// Traccar's built-in engineStop/engineResume.
        /// </summary>
        private static (string Type, Dictionary<string, object> Attributes) MapToTraccarCommand(DeviceCommandType type) =>
            type switch
            {
                DeviceCommandType.Unlock => ("outputControl", new Dictionary<string, object> { ["index"] = 1, ["data"] = false }),
                DeviceCommandType.Lock => ("outputControl", new Dictionary<string, object> { ["index"] = 1, ["data"] = true }),
                DeviceCommandType.Immobilize => ("engineStop", new Dictionary<string, object>()),
                DeviceCommandType.Mobilize => ("engineResume", new Dictionary<string, object>()),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown device command type."),
            };

        private async Task ResolveAsync(
            DeviceCommand command,
            DeviceCommandState state,
            string result,
            string? positionSnapshotJson,
            CancellationToken ct)
        {
            command.State = state;
            command.ResolvedAt = DateTime.UtcNow;

            _db.CommandAudits.Add(new CommandAudit
            {
                Id = Guid.NewGuid(),
                DeviceCommandId = command.Id,
                Result = Truncate(result, 255) ?? string.Empty,
                PositionSnapshotJson = positionSnapshotJson,
                CreatedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[DeviceCommand] {CommandId} vehicleId={VehicleId} type={Type} requestedBy={UserId} -> {State}: {Result}",
                command.Id, command.VehicleId, command.Type, command.RequestedByUserId, state, result);
        }

        private static string? Truncate(string? value, int maxLength) =>
            string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
    }
}
