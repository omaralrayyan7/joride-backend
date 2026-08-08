namespace JoRideBackend.Models.Payments
{
    public enum PaymentIntentState
    {
        Created,
        Authorized,
        Captured,
        Voided,
        Refunded,
        Failed
    }

    public class PaymentIntent
    {
        public Guid Id { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public PaymentIntentState State { get; set; } = PaymentIntentState.Created;
        public string? ProviderRef { get; set; }
        public int? TripId { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
