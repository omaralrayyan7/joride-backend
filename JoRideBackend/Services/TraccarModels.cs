using System.Text.Json.Serialization;

namespace JoRideBackend.Services
{
    public class TraccarDevice
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public string? Status { get; set; }
    }

    public class TraccarPosition
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public string? Protocol { get; set; }
        public DateTime ServerTime { get; set; }
        public DateTime DeviceTime { get; set; }
        public DateTime FixTime { get; set; }
        public bool Valid { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public double Speed { get; set; }
        public double Course { get; set; }
        public string? Address { get; set; }
    }

    /// <summary>
    /// Mirrors org.traccar.model.Event as forwarded by Traccar's event.forward.url —
    /// see https://www.traccar.org/forward/. Event type constants we care about:
    /// deviceOnline, deviceOffline, deviceMoving, deviceStopped, ignitionOn, ignitionOff.
    /// </summary>
    public class TraccarEvent
    {
        public long Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public DateTime EventTime { get; set; }
        public long DeviceId { get; set; }
        public long? PositionId { get; set; }
        public long? GeofenceId { get; set; }
        public long? MaintenanceId { get; set; }
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// The full JSON body Traccar POSTs to event.forward.url: { "event": {...},
    /// "position": {...}, "device": {...} }. Position/device are included by
    /// Traccar for context but are optional from our side (e.g. geofence events
    /// may omit position).
    /// </summary>
    public class TraccarEventWebhookPayload
    {
        public TraccarEvent? Event { get; set; }
        public TraccarPosition? Position { get; set; }
        public TraccarDevice? Device { get; set; }
    }
}
