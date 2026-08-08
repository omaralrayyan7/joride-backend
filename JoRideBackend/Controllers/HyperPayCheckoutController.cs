using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Amount is required; TripId is optional and, per PaymentIntent's actual schema
/// (Models/Payments/PaymentIntent.cs), the only linkage it supports — there is no
/// "topUpId" concept anywhere in PaymentIntent, so a checkout with no TripId is just a
/// standalone card payment (e.g. a wallet top-up), not tied to any PendingTopUp row
/// (PendingTopUp is the unrelated, separate manual-reconciliation flow — see
/// WalletController.TopUp/AdminPaymentsController).
/// </summary>
public record CreateHyperPayCheckoutRequest(int UserId, decimal Amount, int? TripId = null, string Currency = "USD");

/// <summary>
/// Starts a HyperPay Copy&amp;Pay checkout: creates a PaymentIntent (Created state — this
/// does not move any money) and asks the gateway to prepare a checkout for it, returning
/// what the frontend needs to render OPPWA's hosted payment widget. Completing that widget
/// is a separate step the payer does client-side; the resulting authorization is picked up
/// later by HyperPayWebhookService (the checkout's merchantTransactionId is this
/// PaymentIntent's own Id, which is how that webhook correlates back to it — see
/// HyperPayGateway.CreateCheckoutAsync).
/// </summary>
[ApiController]
[Route("api/payments/hyperpay")]
[Authorize]
public class HyperPayCheckoutController : ControllerBase
{
    private readonly PaymentsDbContext _db;
    private readonly IPaymentGateway _gateway;

    public HyperPayCheckoutController(PaymentsDbContext db, IPaymentGateway gateway)
    {
        _db = db;
        _gateway = gateway;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateHyperPayCheckoutRequest request, CancellationToken ct)
    {
        // Ownership check, matching TripsController.Start/Cancel: a caller may only start a
        // checkout for themselves unless they're an admin — otherwise any authenticated user
        // could initiate a charge attributed to an arbitrary other UserId.
        var callerId = User.FindFirst("sub")?.Value;
        var isAdmin = User.HasClaim("role", "admin");
        if (!isAdmin && (callerId is null || !int.TryParse(callerId, out var callerIdInt) || callerIdInt != request.UserId))
        {
            return Forbid();
        }

        if (request.Amount <= 0) return BadRequest("Amount must be greater than zero.");
        if (!UsersController.Exists(request.UserId)) return BadRequest("User not found");

        if (request.TripId.HasValue)
        {
            var trip = TripsController.GetTrip(request.TripId.Value);
            if (trip is null) return BadRequest("Trip not found");
            if (trip.UserId != request.UserId) return BadRequest("Trip does not belong to this user.");
        }

        var intent = new PaymentIntent
        {
            Id = Guid.NewGuid(),
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency,
            UserId = request.UserId,
            TripId = request.TripId,
            State = PaymentIntentState.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Persisted before calling the gateway: CreateCheckoutAsync sends intent.Id as
        // HyperPay's merchantTransactionId, so the row needs to exist before anything comes
        // back that could reference it (and so a failed gateway call still leaves an
        // auditable Failed record instead of nothing at all).
        _db.PaymentIntents.Add(intent);
        await _db.SaveChangesAsync(ct);

        PaymentCheckoutResult checkout;
        try
        {
            checkout = await _gateway.CreateCheckoutAsync(intent, ct);
        }
        catch (Exception ex)
        {
            intent.TransitionTo(PaymentIntentState.Failed);
            await _db.SaveChangesAsync(ct);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Failed to create checkout with payment provider.", detail = ex.Message });
        }

        return Ok(new
        {
            paymentIntentId = intent.Id,
            checkoutId = checkout.CheckoutId,
            widgetUrl = checkout.WidgetOrRedirectUrl,
            amount = intent.Amount,
            currency = intent.Currency,
            state = intent.State.ToString(),
        });
    }
}
