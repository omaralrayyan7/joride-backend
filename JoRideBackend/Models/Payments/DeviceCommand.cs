namespace JoRideBackend.Models.Payments
{
    public enum DeviceCommandType
    {
        Unlock,
        Lock,
        Immobilize,
        Mobilize
    }

    public enum DeviceCommandState
    {
        Queued,
        Sent,
        Confirmed,
        TimedOut,
        Failed,

        /// <summary>Rejected by the Immobilize safety gate without ever calling Traccar.</summary>
        SafetyBlocked,

        /// <summary>Rejected because the caller was not authorized to issue device commands.</summary>
        Unauthorized
    }

    public class DeviceCommand
    {
        public Guid Id { get; set; }
        public int VehicleId { get; set; }
        public string ImeiOrDeviceId { get; set; } = string.Empty;
        public DeviceCommandType Type { get; set; }
        public DeviceCommandState State { get; set; } = DeviceCommandState.Queued;
        public int RequestedByUserId { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
