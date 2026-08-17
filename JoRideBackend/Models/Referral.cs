namespace JoRideBackend.Models
{
    public class Referral
    {
        public int Id { get; set; }
        public int ReferrerId { get; set; }
        public int ReferredUserId { get; set; }
        public string Code { get; set; } = string.Empty;
        public decimal RewardAmount { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
