namespace JoRideBackend.Models
{
    public class TripRating
    {
        public int TripId { get; set; }
        public int UserId { get; set; }
        public int VehicleId { get; set; }
        public int Stars { get; set; }
        public string? Comment { get; set; }
        public string? ConditionPhotoUrl { get; set; }
        public DateTime SubmittedAt { get; set; }
    }
}
