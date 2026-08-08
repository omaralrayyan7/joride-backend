using System.Globalization;
using System.Security.Claims;
using System.Text;
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

    /// <summary>
    /// Read-only weekly payout report as a downloadable CSV. Per-vehicle, not per-owner
    /// (see PaymentAdminService.GeneratePayoutReportAsync — no owner concept exists in the
    /// schema yet). Writes no ledger entries and marks nothing as paid.
    /// </summary>
    [HttpGet("payouts/report")]
    public async Task<IActionResult> PayoutReport(
        [FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd, CancellationToken ct)
    {
        var (adminId, adminLabel) = GetActor();

        IReadOnlyList<PayoutReportRow> rows;
        try
        {
            rows = await _paymentAdmin.GeneratePayoutReportAsync(periodStart, periodEnd, adminId, adminLabel, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var csv = BuildCsv(rows, periodStart, periodEnd);
        var fileName = $"payout-report-{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}.csv";
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }

    private static string BuildCsv(IReadOnlyList<PayoutReportRow> rows, DateTime periodStart, DateTime periodEnd)
    {
        var sb = new StringBuilder();

        // TODO(business decision): no owner/partner concept exists in the schema yet
        // (Vehicle has no OwnerId or similar) — rows below are grouped per-vehicle as a
        // placeholder. Switch to owner grouping once that concept lands.
        sb.AppendLine("# NOTE: grouped per-vehicle — no owner/partner concept exists in the schema yet.");
        // TODO(business decision): no platform fee percentage has been configured anywhere
        // (Payouts:PlatformFeePercent) — defaults to 0% until product/finance sets one.
        sb.AppendLine("# NOTE: PlatformFee uses Payouts:PlatformFeePercent, defaulting to 0% (not yet set — business decision pending).");

        sb.AppendLine("OwnerOrVehicleId,Name/Plate,PeriodStart,PeriodEnd,GrossRevenue,PlatformFee,NetPayout");
        foreach (var row in rows)
        {
            sb.Append(CsvField(row.VehicleId?.ToString(CultureInfo.InvariantCulture) ?? "unassigned")).Append(',')
              .Append(CsvField(row.NameOrPlate)).Append(',')
              .Append(CsvField(periodStart.ToString("O"))).Append(',')
              .Append(CsvField(periodEnd.ToString("O"))).Append(',')
              .Append(CsvField(row.GrossRevenue.ToString("F2", CultureInfo.InvariantCulture))).Append(',')
              .Append(CsvField(row.PlatformFee.ToString("F2", CultureInfo.InvariantCulture))).Append(',')
              .Append(CsvField(row.NetPayout.ToString("F2", CultureInfo.InvariantCulture)))
              .Append('\n');
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private (int AdminId, string AdminLabel) GetActor()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "0";
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "admin";
        int.TryParse(idStr, out var id);
        return (id, $"Admin: {name} (#{idStr})");
    }
}
