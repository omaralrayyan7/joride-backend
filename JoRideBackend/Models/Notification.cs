namespace JoRideBackend.Models
{
    public class Notification
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? Type { get; set; }  // "booking", "payment", "system", "promo"
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
