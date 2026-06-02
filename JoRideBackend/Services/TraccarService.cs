using System.Globalization;

namespace JoRideBackend.Services
{
    /// <summary>
    /// Sends vehicle data to Traccar using the OsmAnd HTTP protocol.
    /// Traccar listens on http://localhost:5055 for OsmAnd-format position reports.
    ///
    /// Each vehicle is identified by its license plate as the Traccar device identifier.
    /// Custom attributes (event, status, locked) are forwarded as extra query params
    /// which Traccar stores and shows in the "Attributes" panel per position.
    /// </summary>
    public class TraccarService
    {
        private readonly HttpClient _http;
        private readonly ILogger<TraccarService> _logger;

        // Traccar OsmAnd port (default 5055) — NOT the web UI port (8082)
        private const string TraccarOsmAndUrl = "http://localhost:5055";

        public TraccarService(IHttpClientFactory factory, ILogger<TraccarService> logger)
        {
            _http = factory.CreateClient("traccar");
            _logger = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Send the current GPS position of a vehicle to Traccar.
        /// Call this whenever you have a fresh location (e.g. from VehicleLocationController).
        /// </summary>
        public Task SendLocationAsync(
            string deviceId,        // e.g. licence plate "JO-AMM01"
            double lat,
            double lon,
            double speed = 0,
            double fuel = 0,
            string status = "Available")
        {
            var extras = new Dictionary<string, string>
            {
                ["status"] = status,
                ["fuel"]   = fuel.ToString("F1", CultureInfo.InvariantCulture),
            };
            return PostAsync(deviceId, lat, lon, speed, extras);
        }

        /// <summary>
        /// Notify Traccar that a vehicle has been booked.
        /// Sends a position update with event=booked and status=InUse.
        /// </summary>
        public Task SendBookingEventAsync(
            string deviceId,
            double lat,
            double lon,
            int tripId,
            int userId)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]  = "booked",
                ["status"] = "InUse",
                ["tripId"] = tripId.ToString(),
                ["userId"] = userId.ToString(),
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        /// <summary>
        /// Notify Traccar that a vehicle has been unlocked by the driver.
        /// </summary>
        public Task SendUnlockEventAsync(string deviceId, double lat, double lon, int tripId)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]  = "unlocked",
                ["status"] = "InUse",
                ["locked"] = "false",
                ["tripId"] = tripId.ToString(),
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        /// <summary>
        /// Notify Traccar that a vehicle has been locked by the driver.
        /// </summary>
        public Task SendLockEventAsync(string deviceId, double lat, double lon, int tripId)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]  = "locked",
                ["status"] = "InUse",
                ["locked"] = "true",
                ["tripId"] = tripId.ToString(),
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        /// <summary>
        /// Notify Traccar that a trip has ended and the vehicle is available again.
        /// </summary>
        public Task SendTripEndedEventAsync(string deviceId, double lat, double lon, int tripId)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]  = "trip_ended",
                ["status"] = "Available",
                ["locked"] = "true",
                ["tripId"] = tripId.ToString(),
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        /// <summary>
        /// Admin remote control: start or kill the engine (immobilizer) of a vehicle.
        /// </summary>
        public Task SendEngineEventAsync(string deviceId, double lat, double lon, bool engineOn)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]    = engineOn ? "engine_start" : "engine_kill",
                ["engineOn"] = engineOn ? "true" : "false",
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        /// <summary>
        /// Admin remote control: lock or unlock a vehicle (not tied to a trip).
        /// </summary>
        public Task SendAdminLockEventAsync(string deviceId, double lat, double lon, bool locked)
        {
            var extras = new Dictionary<string, string>
            {
                ["event"]  = locked ? "admin_locked" : "admin_unlocked",
                ["locked"] = locked ? "true" : "false",
            };
            return PostAsync(deviceId, lat, lon, 0, extras);
        }

        // ── Core sender ───────────────────────────────────────────────────────

        private async Task PostAsync(
            string deviceId,
            double lat,
            double lon,
            double speed,
            Dictionary<string, string> extras)
        {
            try
            {
                // OsmAnd protocol: GET request with query parameters.
                // Traccar matches the device by the "id" param.
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var queryParams = new Dictionary<string, string>
                {
                    ["id"]        = deviceId,
                    ["lat"]       = lat.ToString("F6", CultureInfo.InvariantCulture),
                    ["lon"]       = lon.ToString("F6", CultureInfo.InvariantCulture),
                    ["speed"]     = speed.ToString("F1", CultureInfo.InvariantCulture),
                    ["timestamp"] = timestamp.ToString(),
                    ["hdop"]      = "1",   // horizontal dilution of precision (good = 1)
                    ["altitude"]  = "800", // Amman average altitude in metres
                };

                // Merge extras
                foreach (var kv in extras)
                    queryParams[kv.Key] = kv.Value;

                var qs = string.Join("&", queryParams.Select(
                    kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

                var url = $"{TraccarOsmAndUrl}/?{qs}";
                var response = await _http.GetAsync(url);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation(
                        "[Traccar] OK — device={Device} event={Event} lat={Lat} lon={Lon}",
                        deviceId,
                        extras.GetValueOrDefault("event", "location"),
                        lat, lon);
                else
                    _logger.LogWarning(
                        "[Traccar] Non-success {Status} for device={Device}",
                        (int)response.StatusCode, deviceId);
            }
            catch (Exception ex)
            {
                // Never let Traccar errors bubble up and break the main flow
                _logger.LogError(ex, "[Traccar] Failed to send data for device={Device}", deviceId);
            }
        }
    }
}
