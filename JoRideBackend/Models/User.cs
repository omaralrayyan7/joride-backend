namespace JoRideBackend.Models
{
    public class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? PasswordHash { get; set; }
        public string? Phone { get; set; }
        public string? IdNumber { get; set; }
        public string? DrivingLicenseNumber { get; set; }
        public bool IsActive { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsLicenseVerified { get; set; }
        public bool IsEmailVerified { get; set; }
        public bool IsPhoneVerified { get; set; }
        public DateTime CreatedAt { get; set; }
        public decimal WalletBalance { get; set; } = 0m;
        public string? ProfileImageUrl { get; set; }

        // ── Brute-force / lockout ──────────────────────────────────────────────
        public int FailedLoginAttempts { get; set; } = 0;
        public DateTime? LockoutEndUtc { get; set; }
    }
}
