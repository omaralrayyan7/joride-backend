namespace JoRideBackend.Models.Payments
{
    public class CommandAudit
    {
        public Guid Id { get; set; }
        public Guid DeviceCommandId { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? PositionSnapshotJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
