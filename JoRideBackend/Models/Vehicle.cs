namespace JoRideBackend.Models
{
    public class Vehicle
    {
        public int Id { get; set; }
        public string? LicensePlate { get; set; }
        public string? Model { get; set; }
        public string? Category { get; set; }
        public string? Color { get; set; }
        public string? Status { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int FuelLevel { get; set; } = 100;
        public string? ImageUrl { get; set; }

        /// <summary>
        /// URL of the car manufacturer brand logo (shown in car cards and details).
        /// Populated from the Clearbit Logo API or a static CDN.
        /// </summary>
        public string? BrandLogoUrl { get; set; }

        /// <summary>
        /// When false the vehicle is hidden from the map / available list.
        /// Admins can toggle this via PUT /api/admin/vehicles/{id}/show|hide.
        /// </summary>
        public bool IsVisible { get; set; } = true;
    }
}
