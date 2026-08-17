using JoRideBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api")]
public class ReceiptsController : ControllerBase
{
    [Authorize]
    [HttpGet("trips/{id:int}/receipt")]
    public IActionResult GetReceipt(int id)
    {
        var trip = TripsController.GetTrip(id);
        if (trip is null) return NotFound();

        var callerStr = User.FindFirst("sub")?.Value;
        var isAdmin   = User.HasClaim("role", "admin");
        if (!isAdmin && (callerStr is null || !int.TryParse(callerStr, out var cid) || cid != trip.UserId))
            return Forbid();

        if (trip.Status != "Completed" && trip.Status != "Cancelled")
            return BadRequest("Receipt is only available for completed or cancelled trips.");

        return Ok(BuildReceipt(trip));
    }

    [Authorize]
    [HttpGet("users/{userId:int}/receipts")]
    public IActionResult GetUserReceipts(int userId)
    {
        var callerStr = User.FindFirst("sub")?.Value;
        var isAdmin   = User.HasClaim("role", "admin");
        if (!isAdmin && (callerStr is null || !int.TryParse(callerStr, out var cid) || cid != userId))
            return Forbid();

        if (!UsersController.Exists(userId)) return NotFound();

        var receipts = TripsController.AllTrips()
            .Where(t => t.UserId == userId && (t.Status == "Completed" || t.Status == "Cancelled"))
            .OrderByDescending(t => t.StartTime)
            .Select(BuildReceipt);

        return Ok(receipts);
    }

    private static object BuildReceipt(Trip t)
    {
        var user    = UsersController.GetUser(t.UserId);
        var vehicle = VehiclesController.GetVehicleById(t.VehicleId);
        return new
        {
            receiptNumber = $"JR-{t.Id:D6}",
            tripId        = t.Id,
            issuedAt      = t.EndTime ?? t.StartTime,
            user = user is null ? null : new { user.Id, user.Name, user.Email },
            vehicle = vehicle is null ? null : new
            {
                vehicle.Id,
                vehicle.LicensePlate,
                vehicle.Model,
                vehicle.Category,
            },
            billing = new
            {
                t.BaseFare,
                t.BookingFee,
                t.Tax,
                t.DiscountPercent,
                t.DiscountAmount,
                t.OvertimeFare,
                t.TotalFare,
                t.PaymentMethod,
                t.PaymentStatus,
                t.PaidAt,
            },
            trip = new
            {
                t.StartTime,
                t.EndTime,
                t.Duration,
                t.DurationType,
                t.Status,
            },
        };
    }
}
