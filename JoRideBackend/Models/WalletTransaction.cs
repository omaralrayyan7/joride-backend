namespace JoRideBackend.Models
{
    public class WalletTransaction
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string? Type { get; set; }  // "topup", "payment", "refund"
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
