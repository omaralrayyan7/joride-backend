using JoRideBackend.Data;
using JoRideBackend.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.Auth;

public class RefreshTokenServiceTests
{
    private static (PaymentsDbContext Db, RefreshTokenService Service) CreateSut()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var service = new RefreshTokenService(db, NullLogger<RefreshTokenService>.Instance);
        return (db, service);
    }

    [Fact]
    public async Task IssueAsync_creates_a_token_that_rotates_successfully()
    {
        var (db, service) = CreateSut();

        var raw = await service.IssueAsync(userId: 1, createdByIp: "127.0.0.1");
        Assert.False(string.IsNullOrWhiteSpace(raw));

        var result = await service.RotateAsync(raw, "127.0.0.1");

        Assert.Equal(RefreshOutcome.Rotated, result.Outcome);
        Assert.Equal(1, result.UserId);
        Assert.False(string.IsNullOrWhiteSpace(result.RawToken));
        Assert.NotEqual(raw, result.RawToken); // a genuinely new token, not the same one reissued
    }

    [Fact]
    public async Task Rotation_revokes_the_presented_token()
    {
        var (db, service) = CreateSut();
        var raw = await service.IssueAsync(userId: 2, createdByIp: null);

        await service.RotateAsync(raw, null);

        // Two rows now exist (original + rotated) — the original is the earliest by CreatedAt.
        var originalRow = await db.RefreshTokens.Where(t => t.UserId == 2).OrderBy(t => t.CreatedAt).FirstAsync();
        Assert.NotNull(originalRow.RevokedAt);
        Assert.NotNull(originalRow.ReplacedByTokenHash);
    }

    [Fact]
    public async Task Reusing_an_already_rotated_token_revokes_the_whole_family()
    {
        var (db, service) = CreateSut();
        var raw1 = await service.IssueAsync(userId: 3, createdByIp: null);

        var firstRotation = await service.RotateAsync(raw1, null); // raw1 now revoked, raw2 issued
        Assert.Equal(RefreshOutcome.Rotated, firstRotation.Outcome);
        var raw2 = firstRotation.RawToken!;

        // Present the already-revoked raw1 again — simulates a stolen/replayed token.
        var reuseResult = await service.RotateAsync(raw1, null);

        Assert.Equal(RefreshOutcome.ReusedRevoked, reuseResult.Outcome);
        Assert.Equal(3, reuseResult.UserId);

        // The whole family — including raw2, which was never itself misused — must now be dead.
        var secondAttemptOnRaw2 = await service.RotateAsync(raw2, null);
        Assert.NotEqual(RefreshOutcome.Rotated, secondAttemptOnRaw2.Outcome);

        var allTokensForUser = await db.RefreshTokens.Where(t => t.UserId == 3).ToListAsync();
        Assert.All(allTokensForUser, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        var (_, service) = CreateSut();

        var result = await service.RotateAsync("this-was-never-issued", null);

        Assert.Equal(RefreshOutcome.InvalidOrUnknown, result.Outcome);
        Assert.Null(result.RawToken);
    }

    [Fact]
    public async Task Expired_token_is_rejected_without_rotating()
    {
        var (db, service) = CreateSut();
        var raw = await service.IssueAsync(userId: 4, createdByIp: null);

        var stored = await db.RefreshTokens.SingleAsync(t => t.UserId == 4);
        stored.ExpiresAt = DateTime.UtcNow.AddDays(-1); // force expiry
        await db.SaveChangesAsync();

        var result = await service.RotateAsync(raw, null);

        Assert.Equal(RefreshOutcome.Expired, result.Outcome);
        Assert.Null(result.RawToken);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_revokes_every_active_token()
    {
        var (db, service) = CreateSut();
        await service.IssueAsync(userId: 5, createdByIp: null);
        await service.IssueAsync(userId: 5, createdByIp: null);

        await service.RevokeAllForUserAsync(5);

        var tokens = await db.RefreshTokens.Where(t => t.UserId == 5).ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }
}
