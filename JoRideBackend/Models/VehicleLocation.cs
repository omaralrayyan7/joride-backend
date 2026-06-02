namespace JoRideBackend.Models
{
    public class VehicleLocation
    {
        public int Id { get; set; }
        public int VehicleId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Speed { get; set; }
        public double Fuel { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public record VehicleLocationRequest(int VehicleId, double Latitude, double Longitude, double Speed, double Fuel);
}
