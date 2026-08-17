namespace JoRideBackend.Models.Auth
{
    /// <summary>
    /// Never stores the raw token — only a SHA-256 hash of it (same principle as
    /// RefreshToken). Single-use: ConsumeAsync sets UsedAt so a token can never be replayed
    /// once it has reset a password, and issuing a new token for a user invalidates any
    /// earlier outstanding one.
    /// </summary>
    public class PasswordResetToken
    {
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public string? CreatedByIp { get; set; }

        public bool IsActive => UsedAt is null && ExpiresAt > DateTime.UtcNow;
    }
}
