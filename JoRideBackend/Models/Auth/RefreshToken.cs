namespace JoRideBackend.Models.Auth
{
    /// <summary>
    /// Never stores the raw refresh token — only a SHA-256 hash of it (same principle as a
    /// password: possession of the DB row must not be enough to impersonate the user).
    /// FamilyId links every token produced by rotating from the same original login, so
    /// reusing an already-rotated (and therefore revoked) token — a signal of theft — can
    /// revoke the whole chain at once, not just the one stolen token.
    /// </summary>
    public class RefreshToken
    {
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public Guid FamilyId { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? ReplacedByTokenHash { get; set; }
        public string? CreatedByIp { get; set; }

        public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
    }
}
