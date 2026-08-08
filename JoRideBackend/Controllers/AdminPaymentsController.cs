using System.Security.Claims;
using JoRideBackend.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

public record PartialCaptureRequest(decimal Amount);
public record RejectTopUpRequest(string Reason);

/// <summary>
/// Admin-only money actions needing a human in the loop: partial capture and manual top-up
/// reconciliation. See PaymentAdminService for the actual logic and design rationale — this
/// controller only extracts the caller's admin identity and maps outcomes to HTTP status.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminPaymentsController : ControllerBase
{
    private readonly PaymentAdminService _paymentAdmin;

    public AdminPaymentsController(PaymentAdminService paymentAdmin)
    {
        _paymentAdmin = paymentAdmin;
    }

    [HttpPost("payment-intents/{id:guid}/partial-capture")]
    public async Task<IActionResult> PartialCapture(Guid id, [FromBody] PartialCaptureRequest request, CancellationToken ct)
    {
        var (adminId, adminLabel) = GetActor();

        try
        {
            var result = await _paymentAdmin.PartialCaptureAsync(id, request.Amount, adminId, adminLabel, ct);
            return Ok(new
            {
                paymentIntentId = result.Intent.Id,
                state = result.Intent.State.ToString(),
                capturedAmount = result.CapturedAmount,
                releasedAmount = result.ReleasedAmount,
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("topups/{id:guid}/confirm")]
    public async Task<IActionResult> ConfirmTopUp(Guid id, CancellationToken ct)
    {
        var (adminId, adminLabel) = GetActor();

        try
        {
            var topUp = await _paymentAdmin.ConfirmTopUpAsync(id, adminId, adminLabel, ct);
            return Ok(new { id = topUp.Id, status = topUp.Status.ToString(), amount = topUp.Amount, userId = topUp.UserId });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("topups/{id:guid}/reject")]
    public async Task<IActionResult> RejectTopUp(Guid id, [FromBody] RejectTopUpRequest request, CancellationToken ct)
    {
        var (adminId, adminLabel) = GetActor();

        try
        {
            var topUp = await _paymentAdmin.RejectTopUpAsync(id, adminId, adminLabel, request.Reason, ct);
            return Ok(new { id = topUp.Id, status = topUp.Status.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    private (int AdminId, string AdminLabel) GetActor()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "0";
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "admin";
        int.TryParse(idStr, out var id);
        return (id, $"Admin: {name} (#{idStr})");
    }
}
