using JoRideBackend.Data;
using JoRideBackend.Models.Telemetry;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JoRideBackend.Services
{
    /// <summary>
    /// Periodically polls Traccar's REST API for current device positions, persists each
    /// new one as a TelemetrySnapshot, and mirrors it onto the matching vehicle's live
    /// lat/lng in Firestore.
    /// </summary>
    public class TraccarPollingService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly TraccarService _traccar;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TraccarPollingService> _logger;

        public TraccarPollingService(
            TraccarService traccar, IServiceScopeFactory scopeFactory, ILogger<TraccarPollingService> logger)
        {
            _traccar = traccar;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_traccar.IsRestConfigured)
            {
                _logger.LogWarning(
                    "[TraccarPolling] TRACCAR_BASE_URL/TRACCAR_TOKEN not configured — position polling disabled.");
                return;
            }

            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await PollOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task PollOnceAsync(CancellationToken ct)
        {
            try
            {
                var positions = await _traccar.GetAllPositionsAsync(ct);
                if (positions.Count == 0)
                    return;

                // Resolve deviceId -> our VehicleId via uniqueId == LicensePlate (same
                // convention TraccarService.SendLocationAsync already uses for pushes).
                var devices = await _traccar.GetDevicesAsync(ct);
                var uniqueIdByDeviceId = devices.ToDictionary(d => d.Id, d => d.UniqueId);
                var vehicleIdByLicensePlate = VehiclesController.AllVehicles()
                    .Where(v => !string.IsNullOrWhiteSpace(v.LicensePlate))
                    .ToDictionary(v => v.LicensePlate!, v => v.Id, StringComparer.OrdinalIgnoreCase);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

                foreach (var position in positions)
                {
                    int? vehicleId = null;
                    if (uniqueIdByDeviceId.TryGetValue(position.DeviceId, out var uniqueId) &&
                        vehicleIdByLicensePlate.TryGetValue(uniqueId, out var matchedVehicleId))
                    {
                        vehicleId = matchedVehicleId;
                    }

                    _logger.LogInformation(
                        "[TraccarPolling] deviceId={DeviceId} vehicleId={VehicleId} deviceTime={DeviceTime:o} lat={Lat} lon={Lon} speed={Speed}",
                        position.DeviceId, vehicleId, position.DeviceTime, position.Latitude, position.Longitude, position.Speed);

                    db.TelemetrySnapshots.Add(new TelemetrySnapshot
                    {
                        Id = Guid.NewGuid(),
                        DeviceId = position.DeviceId,
                        // Npgsql requires Kind=Utc for timestamptz; System.Text.Json hands
                        // back Local-kind DateTimes for offset timestamps, so normalize here.
                        DeviceTime = position.DeviceTime.ToUniversalTime(),
                        Latitude = position.Latitude,
                        Longitude = position.Longitude,
                        Speed = position.Speed,
                        VehicleId = vehicleId,
                        CreatedAt = DateTime.UtcNow,
                    });

                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                    {
                        // Falls back on the DB's unique (DeviceId, DeviceTime) index, e.g. if
                        // the app restarted and TraccarService's in-memory dedup cache reset
                        // while Traccar is still reporting the same last-known position.
                        _logger.LogDebug(ex,
                            "[TraccarPolling] Skipped duplicate snapshot deviceId={DeviceId} deviceTime={DeviceTime:o}.",
                            position.DeviceId, position.DeviceTime);
                        db.ChangeTracker.Clear();
                        continue;
                    }

                    if (vehicleId is not null)
                    {
                        VehiclesController.SetPosition(vehicleId.Value, position.Latitude, position.Longitude);
                    }

                    // No SignalR hub exists in this codebase yet — nothing to broadcast to.
                    // TODO(E7): broadcast the new position on a live-tracking hub once the
                    // dashboard work introduces one.
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down — not an error
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TraccarPolling] Failed to poll Traccar positions.");
            }
        }

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
