namespace JoRideBackend.Models
{
    public class Trip
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int VehicleId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? ScheduledEndTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int Duration { get; set; }
        public string? DurationType { get; set; }
        public decimal BaseFare { get; set; }
        public decimal BookingFee { get; set; }
        public decimal Tax { get; set; }
        public decimal TotalFare { get; set; }
        public int OvertimeMinutes { get; set; }
        public decimal OvertimeFare { get; set; }
        public string? OvertimePaymentStatus { get; set; }
        public string? PaymentMethod { get; set; }
        public string? PaymentStatus { get; set; }
        public DateTime? PaidAt { get; set; }
        public bool DigitalKeyEnabled { get; set; }
        public string? Status { get; set; }
    }
}
