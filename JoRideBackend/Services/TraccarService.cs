using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace JoRideBackend.Services
{
    /// <summary>
    /// Sends vehicle data to Traccar using the OsmAnd HTTP protocol, and reads
    /// device/position data back from Traccar's REST API.
    /// Traccar listens on http://localhost:5055 for OsmAnd-format position reports
    /// (write side) and on TRACCAR_BASE_URL for the REST API (read side).
    ///
    /// Each vehicle is identified by its license plate as the Traccar device identifier.
    /// Custom attributes (event, status, locked) are forwarded as extra query params
    /// which Traccar stores and shows in the "Attributes" panel per position.
    /// </summary>
    public class TraccarService
    {
        private readonly HttpClient _http;
        private readonly HttpClient _restHttp;
        private readonly ILogger<TraccarService> _logger;
        private readonly bool _restConfigured;

        // Traccar OsmAnd port (default 5055) — NOT the web UI port (8082)
        private const string TraccarOsmAndUrl = "http://localhost:5055";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // Last deviceTime seen per deviceId, used to skip duplicate position reads.
        private readonly ConcurrentDictionary<long, DateTime> _lastSeenDeviceTime = new();

        public TraccarService(IHttpClientFactory factory, ILogger<TraccarService> logger, IConfiguration configuration)
        {
            _http = factory.CreateClient("traccar");
            _restHttp = factory.CreateClient("traccar-rest");
            _logger = logger;

            var baseUrl = configuration["TRACCAR_BASE_URL"];
            var token = configuration["TRACCAR_TOKEN"];
            _restConfigured = !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(token);

            if (!_restConfigured)
            {
                _logger.LogWarning(
                    "[Traccar] TRACCAR_BASE_URL/TRACCAR_TOKEN not configured — REST API reads (positions, health) are unavailable.");
            }
        }

        /// <summary>True if TRACCAR_BASE_URL and TRACCAR_TOKEN are both configured.</summary>
        public bool IsRestConfigured => _restConfigured;

        // ── REST read API ────────────────────────────────────────────────────

        /// <summary>Lists all devices registered in Traccar.</summary>
        public async Task<List<TraccarDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            EnsureRestConfigured();
            var response = await _restHttp.GetAsync("api/devices", ct);
            response.EnsureSuccessStatusCode();
            var devices = await response.Content.ReadFromJsonAsync<List<TraccarDevice>>(JsonOptions, ct);
            return devices ?? new List<TraccarDevice>();
        }

        /// <summary>
        /// Fetches the current (latest) position for every device, deduplicated on
        /// (deviceId, deviceTime) against previously seen positions and ordered by
        /// deviceTime ascending (devices can reconnect and report out of order).
        /// </summary>
        public async Task<List<TraccarPosition>> GetAllPositionsAsync(CancellationToken ct = default)
        {
            EnsureRestConfigured();

            var devices = await GetDevicesAsync(ct);
            if (devices.Count == 0)
                return new List<TraccarPosition>();

            var query = string.Join("&", devices.Select(d => $"deviceId={d.Id}"));
            var response = await _restHttp.GetAsync($"api/positions?{query}", ct);
            response.EnsureSuccessStatusCode();
            var positions = await response.Content.ReadFromJsonAsync<List<TraccarPosition>>(JsonOptions, ct);
            return DeduplicateAndOrder(positions ?? new List<TraccarPosition>());
        }

        /// <summary>
        /// Fetches the current (latest) position for a single device — always the real
        /// latest position, regardless of whether the polling loop has already seen it.
        /// Deliberately does NOT go through <see cref="DeduplicateAndOrder"/>: that cache
        /// exists to stop the poller re-logging an unchanged position, but callers here
        /// (the Immobilize safety gate, command audit snapshots) need the actual position
        /// every time — silently returning null for an already-seen-but-still-valid
        /// position would be wrong for both.
        /// </summary>
        public async Task<TraccarPosition?> GetPositionAsync(long deviceId, CancellationToken ct = default)
        {
            EnsureRestConfigured();

            var response = await _restHttp.GetAsync($"api/positions?deviceId={deviceId}", ct);
            response.EnsureSuccessStatusCode();
            var positions = await response.Content.ReadFromJsonAsync<List<TraccarPosition>>(JsonOptions, ct);
            return positions?.OrderByDescending(p => p.DeviceTime).FirstOrDefault();
        }

        /// <summary>Finds a registered Traccar device by its uniqueId (we use the vehicle's license plate).</summary>
        public async Task<TraccarDevice?> FindDeviceByUniqueIdAsync(string uniqueId, CancellationToken ct = default)
        {
            var devices = await GetDevicesAsync(ct);
            return devices.FirstOrDefault(d => string.Equals(d.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sends a command to a device via POST /api/commands/send. Does NOT throw on a
        /// non-success HTTP response — Traccar rejecting a command it doesn't support
        /// (expected for our OsmAnd-protocol test device, which has no command channel)
        /// is a normal, expected outcome, not a transport failure. Network-level failures
        /// (timeouts, connection errors, 5xx) still throw after the traccar-rest client's
        /// Polly retry policy (3x exponential backoff) is exhausted.
        /// </summary>
        public async Task<TraccarCommandResult> SendCommandAsync(
            long deviceId, string type, Dictionary<string, object>? attributes = null, CancellationToken ct = default)
        {
            EnsureRestConfigured();

            var body = new { deviceId, type, attributes = attributes ?? new Dictionary<string, object>() };
            var response = await _restHttp.PostAsJsonAsync("api/commands/send", body, JsonOptions, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return new TraccarCommandResult(response.IsSuccessStatusCode, (int)response.StatusCode, responseBody);
        }

        /// <summary>Used by /health: true if Traccar's REST API is reachable and authenticated.</summary>
        public async Task<bool> CheckServerHealthAsync(CancellationToken ct = default)
        {
            if (!_restConfigured)
                return false;

            try
            {
                var response = await _restHttp.GetAsync("api/server", ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Traccar] Health check failed.");
                return false;
            }
        }

        private List<TraccarPosition> DeduplicateAndOrder(IEnumerable<TraccarPosition> positions)
        {
            var fresh = new List<TraccarPosition>();
            foreach (var position in positions)
            {
                var isDuplicate = _lastSeenDeviceTime.TryGetValue(position.DeviceId, out var lastSeen)
                                   && lastSeen == position.DeviceTime;
                if (isDuplicate)
                    continue;

                _lastSeenDeviceTime[position.DeviceId] = position.DeviceTime;
                fresh.Add(position);
            }

            return fresh.OrderBy(p => p.DeviceTime).ToList();
        }

        private void EnsureRestConfigured()
        {
            if (!_restConfigured)
                throw new InvalidOperationException(
                    "TRACCAR_BASE_URL and TRACCAR_TOKEN must be configured to use the Traccar REST API.");
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
