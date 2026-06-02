namespace JoRideBackend.Models
{
    public class Pricing
    {
        public int Id { get; set; }
        public string? Category { get; set; }
        public decimal MinuteRate { get; set; }
        public decimal HourlyRate { get; set; }
        public decimal DailyRate { get; set; }
        public bool IsActive { get; set; }
    }
}
