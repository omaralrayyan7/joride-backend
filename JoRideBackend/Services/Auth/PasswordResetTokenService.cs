using System.Security.Cryptography;
using JoRideBackend.Data;
using JoRideBackend.Models.Auth;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Auth
{
    public enum PasswordResetOutcome
    {
        Valid,
        InvalidOrUnknown,
        Expired,
        AlreadyUsed,
    }

    public record PasswordResetValidation(PasswordResetOutcome Outcome, PasswordResetToken? Entity);

    /// <summary>
    /// Issues and validates password-reset tokens. Short-lived (1 hour) and single-use — see
    /// RefreshTokenService for the same hash-only-storage principle applied to a longer-lived
    /// token family.
    /// </summary>
    public class PasswordResetTokenService
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

        private readonly PaymentsDbContext _db;

        public PasswordResetTokenService(PaymentsDbContext db)
        {
            _db = db;
        }

        /// <summary>Issues a new token, invalidating any earlier outstanding token for the same user.</summary>
        public async Task<string> IssueAsync(int userId, string? createdByIp, CancellationToken ct = default)
        {
            var earlier = await _db.PasswordResetTokens
                .Where(t => t.UserId == userId && t.UsedAt == null)
                .ToListAsync(ct);
            foreach (var t in earlier)
                t.UsedAt = DateTime.UtcNow;

            var rawBytes = RandomNumberGenerator.GetBytes(32); // 256-bit
            var raw = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // URL-safe
            var now = DateTime.UtcNow;

            var entity = new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = Hash(raw),
                CreatedAt = now,
                ExpiresAt = now.Add(TokenLifetime),
                CreatedByIp = createdByIp,
            };
            _db.PasswordResetTokens.Add(entity);
            await _db.SaveChangesAsync(ct);
            return raw;
        }

        public async Task<PasswordResetValidation> ValidateAsync(string rawToken, CancellationToken ct = default)
        {
            var hash = Hash(rawToken);
            var existing = await _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

            if (existing is null)
                return new PasswordResetValidation(PasswordResetOutcome.InvalidOrUnknown, null);
            if (existing.UsedAt is not null)
                return new PasswordResetValidation(PasswordResetOutcome.AlreadyUsed, existing);
            if (existing.ExpiresAt <= DateTime.UtcNow)
                return new PasswordResetValidation(PasswordResetOutcome.Expired, existing);

            return new PasswordResetValidation(PasswordResetOutcome.Valid, existing);
        }

        public async Task ConsumeAsync(PasswordResetToken entity, CancellationToken ct = default)
        {
            entity.UsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private static string Hash(string rawToken) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
    }
}
