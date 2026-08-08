namespace JoRideBackend.Models.Payments
{
    public class LedgerEntry
    {
        public Guid Id { get; set; }
        public Guid? PaymentIntentId { get; set; }
        public string DebitAccount { get; set; } = string.Empty;
        public string CreditAccount { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string? Reference { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
