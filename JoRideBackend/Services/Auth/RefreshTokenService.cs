using System.Security.Cryptography;
using JoRideBackend.Data;
using JoRideBackend.Models.Auth;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Auth
{
    public enum RefreshOutcome
    {
        Rotated,
        InvalidOrUnknown,
        Expired,
        /// <summary>The token was already used once before (it's revoked but someone
        /// presented it again) — the whole family gets revoked as a precaution.</summary>
        ReusedRevoked,
    }

    public record RefreshResult(RefreshOutcome Outcome, int? UserId, string? RawToken, RefreshToken? Entity);

    /// <summary>
    /// Issues and rotates refresh tokens. Rotation, verified: using a refresh token always
    /// invalidates it and replaces it with a new one in the same call — there is no code
    /// path that returns success without revoking what was just used. Detects reuse of an
    /// already-rotated token (a real signal of token theft) and revokes the entire family.
    /// </summary>
    public class RefreshTokenService
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

        private readonly PaymentsDbContext _db;
        private readonly ILogger<RefreshTokenService> _logger;

        public RefreshTokenService(PaymentsDbContext db, ILogger<RefreshTokenService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>Issues the first refresh token of a new family (e.g. on login/register).</summary>
        public async Task<string> IssueAsync(int userId, string? createdByIp, CancellationToken ct = default)
        {
            var (raw, entity) = CreateToken(userId, Guid.NewGuid(), createdByIp);
            _db.RefreshTokens.Add(entity);
            await _db.SaveChangesAsync(ct);
            return raw;
        }

        /// <summary>
        /// Validates <paramref name="rawToken"/> and, if valid, rotates it: the presented
        /// token is revoked and a new one (same family) is issued in the same call.
        /// </summary>
        public async Task<RefreshResult> RotateAsync(string rawToken, string? requestIp, CancellationToken ct = default)
        {
            var hash = Hash(rawToken);
            var existing = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

            if (existing is null)
            {
                _logger.LogWarning("[RefreshToken] Presented token not found — invalid or unknown.");
                return new RefreshResult(RefreshOutcome.InvalidOrUnknown, null, null, null);
            }

            if (existing.RevokedAt is not null)
            {
                // Someone presented a token we already rotated away from — either a client
                // race (retried an old response) or theft. Revoke the whole family: safer to
                // force a re-login than to let a possibly-stolen chain keep working.
                var familyTokens = await _db.RefreshTokens
                    .Where(t => t.FamilyId == existing.FamilyId && t.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var token in familyTokens)
                {
                    token.RevokedAt = DateTime.UtcNow;
                }
                await _db.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "[RefreshToken] Reuse of a revoked token detected for userId={UserId} familyId={FamilyId} — " +
                    "revoked {Count} active token(s) in the family.",
                    existing.UserId, existing.FamilyId, familyTokens.Count);
                return new RefreshResult(RefreshOutcome.ReusedRevoked, existing.UserId, null, null);
            }

            if (existing.ExpiresAt <= DateTime.UtcNow)
            {
                return new RefreshResult(RefreshOutcome.Expired, existing.UserId, null, null);
            }

            var (newRaw, newEntity) = CreateToken(existing.UserId, existing.FamilyId, requestIp);

            existing.RevokedAt = DateTime.UtcNow;
            existing.ReplacedByTokenHash = newEntity.TokenHash;
            _db.RefreshTokens.Add(newEntity);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[RefreshToken] Rotated for userId={UserId} familyId={FamilyId}.", existing.UserId, existing.FamilyId);

            return new RefreshResult(RefreshOutcome.Rotated, existing.UserId, newRaw, newEntity);
        }

        /// <summary>Revokes every active token for a user (e.g. on logout-everywhere or password change).</summary>
        public async Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
        {
            var tokens = await _db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var token in tokens)
            {
                token.RevokedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }

        private static (string RawToken, RefreshToken Entity) CreateToken(int userId, Guid familyId, string? createdByIp)
        {
            var rawBytes = RandomNumberGenerator.GetBytes(32); // 256-bit
            var raw = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // URL-safe
            var now = DateTime.UtcNow;

            var entity = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FamilyId = familyId,
                TokenHash = Hash(raw),
                CreatedAt = now,
                ExpiresAt = now.Add(TokenLifetime),
                CreatedByIp = createdByIp,
            };

            return (raw, entity);
        }

        private static string Hash(string rawToken) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
    }
}
