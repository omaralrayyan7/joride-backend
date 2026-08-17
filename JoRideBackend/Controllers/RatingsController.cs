using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api")]
public class RatingsController : ControllerBase
{
    static readonly List<TripRating> _ratings = new();
    internal static FirestoreService? _firestore;

    public static void Initialize(List<TripRating> loaded, FirestoreService fs)
    {
        _ratings.Clear();
        _ratings.AddRange(loaded);
        _firestore = fs;
    }

    [Authorize]
    [HttpPost("trips/{id:int}/rating")]
    public async Task<IActionResult> Submit(int id, SubmitRatingRequest request)
    {
        var trip = TripsController.GetTrip(id);
        if (trip is null) return NotFound();

        var callerStr = User.FindFirst("sub")?.Value;
        var isAdmin   = User.HasClaim("role", "admin");
        if (!isAdmin && (callerStr is null || !int.TryParse(callerStr, out var cid) || cid != trip.UserId))
            return Forbid();

        if (trip.Status != "Completed")
            return BadRequest("Ratings can only be submitted for completed trips.");

        if (_ratings.Any(r => r.TripId == id))
            return Conflict("A rating for this trip already exists.");

        var rating = new TripRating
        {
            TripId            = id,
            UserId            = trip.UserId,
            VehicleId         = trip.VehicleId,
            Stars             = request.Stars,
            Comment           = request.Comment,
            ConditionPhotoUrl = request.ConditionPhotoUrl,
            SubmittedAt       = DateTime.UtcNow,
        };
        _ratings.Add(rating);

        await (_firestore?.SaveTripRatingAsync(rating) ?? Task.CompletedTask);
        return Ok(rating);
    }

    [Authorize]
    [HttpGet("trips/{id:int}/rating")]
    public IActionResult Get(int id)
    {
        var trip = TripsController.GetTrip(id);
        if (trip is null) return NotFound();

        var callerStr = User.FindFirst("sub")?.Value;
        var isAdmin   = User.HasClaim("role", "admin");
        if (!isAdmin && (callerStr is null || !int.TryParse(callerStr, out var cid) || cid != trip.UserId))
            return Forbid();

        var rating = _ratings.FirstOrDefault(r => r.TripId == id);
        return rating is null ? NotFound() : Ok(rating);
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("ratings")]
    public IActionResult GetAll()
    {
        var vehicleAverages = _ratings
            .GroupBy(r => r.VehicleId)
            .Select(g => new
            {
                vehicleId    = g.Key,
                averageStars = Math.Round(g.Average(r => r.Stars), 2),
                count        = g.Count(),
            })
            .OrderByDescending(x => x.averageStars);

        return Ok(new { ratings = _ratings, vehicleAverages });
    }
}
