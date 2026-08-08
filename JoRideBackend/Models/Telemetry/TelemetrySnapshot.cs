namespace JoRideBackend.Models.Telemetry
{
    /// <summary>
    /// One persisted position reading from Traccar. Written by TraccarPollingService,
    /// deduplicated on (DeviceId, DeviceTime) — both in-process (TraccarService's dedup
    /// cache) and at the database level (unique index) as defense in depth.
    /// </summary>
    public class TelemetrySnapshot
    {
        public Guid Id { get; set; }
        public long DeviceId { get; set; }
        public DateTime DeviceTime { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Speed { get; set; }

        /// <summary>Our Vehicle.Id, resolved via LicensePlate == Traccar device uniqueId. Null if unmatched.</summary>
        public int? VehicleId { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
