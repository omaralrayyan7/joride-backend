namespace JoRideBackend.Models
{
    public class AuditLog
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>e.g. VehicleLocked, VehicleUnlocked, EngineStarted, EngineKilled, TripBooked, TripEnded.</summary>
        public string? Action { get; set; }

        /// <summary>Vehicle | Trip | User</summary>
        public string? EntityType { get; set; }
        public int EntityId { get; set; }

        /// <summary>Human-readable actor, e.g. "Admin: omar (#3)" or "User: sara (#12)".</summary>
        public string? Actor { get; set; }

        /// <summary>Admin | User | System</summary>
        public string? ActorRole { get; set; }

        public string? Details { get; set; }
    }
}
