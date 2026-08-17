using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/loyalty")]
public class LoyaltyController : ControllerBase
{
    // Tiers: Bronze 0–4 trips (0%), Silver 5–14 (5%), Gold 15–29 (10%), Platinum 30+ (15%)
    public static (string Tier, int DiscountPercent, int TripsToNext) ComputeTier(int userId)
    {
        var count = TripsController.AllTrips()
            .Count(t => t.UserId == userId && t.Status == "Completed");

        return count switch
        {
            < 5  => ("Bronze",   0,  5  - count),
            < 15 => ("Silver",   5,  15 - count),
            < 30 => ("Gold",     10, 30 - count),
            _    => ("Platinum", 15, 0),
        };
    }

    [Authorize]
    [HttpGet("{userId:int}")]
    public IActionResult Get(int userId)
    {
        var callerStr = User.FindFirst("sub")?.Value;
        var isAdmin   = User.HasClaim("role", "admin");
        if (!isAdmin && (callerStr is null || !int.TryParse(callerStr, out var cid) || cid != userId))
            return Forbid();

        if (!UsersController.Exists(userId)) return NotFound();

        var completedCount = TripsController.AllTrips()
            .Count(t => t.UserId == userId && t.Status == "Completed");

        var (tier, discountPercent, tripsToNext) = ComputeTier(userId);

        return Ok(new
        {
            userId,
            tier,
            completedTrips  = completedCount,
            discountPercent,
            tripsToNextTier = tripsToNext,
        });
    }
}
