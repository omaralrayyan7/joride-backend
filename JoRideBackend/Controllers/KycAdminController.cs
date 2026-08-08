using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using JoRideBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

public record KycReviewRequest([Required, StringLength(1000, MinimumLength = 1)] string Reason);

/// <summary>
/// Admin review of a user's KYC status. Uses AuditController — the existing
/// general-purpose (Firestore-backed) audit trail already used throughout this codebase for
/// non-money admin actions — rather than PaymentAdminAudit (Postgres, scoped to money
/// actions) or a new bespoke table; this is a better fit and avoids a third audit mechanism.
/// </summary>
[ApiController]
[Route("api/admin/kyc")]
[Authorize(Policy = "AdminOnly")]
public class KycAdminController : ControllerBase
{
    [HttpPost("{userId:int}/approve")]
    public async Task<IActionResult> Approve(int userId, [FromBody] KycReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "A reason is required." });

        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound();

        var previousStatus = user.KycStatus;
        user.KycStatus = KycStatus.Approved;
        await UsersController.SaveUser(user);

        AuditController.Log("KycApproved", "User", userId, GetActorLabel(), "Admin",
            $"KYC status changed {previousStatus} -> Approved. Reason: {request.Reason}");

        return Ok(new { userId, kycStatus = user.KycStatus.ToString() });
    }

    [HttpPost("{userId:int}/reject")]
    public async Task<IActionResult> Reject(int userId, [FromBody] KycReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "A reason is required." });

        var user = UsersController.GetUser(userId);
        if (user is null) return NotFound();

        var previousStatus = user.KycStatus;
        user.KycStatus = KycStatus.Rejected;
        await UsersController.SaveUser(user);

        AuditController.Log("KycRejected", "User", userId, GetActorLabel(), "Admin",
            $"KYC status changed {previousStatus} -> Rejected. Reason: {request.Reason}");

        return Ok(new { userId, kycStatus = user.KycStatus.ToString() });
    }

    [HttpGet("pending")]
    public IActionResult Pending()
    {
        var pending = UsersController.AllUsers()
            .Where(u => u.KycStatus == KycStatus.Pending)
            .Select(u => new { u.Id, u.Name, u.Email, u.DrivingLicenseNumber, u.IdNumber })
            .ToList();
        return Ok(pending);
    }

    private string GetActorLabel()
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "?";
        var actorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "admin";
        return $"Admin: {actorName} (#{actorId})";
    }
}
