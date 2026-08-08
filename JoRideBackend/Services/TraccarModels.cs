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
}
