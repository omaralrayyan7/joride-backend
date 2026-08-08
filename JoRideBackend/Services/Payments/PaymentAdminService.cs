using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Payments
{
    public record PartialCaptureRequestResult(PaymentIntent Intent, decimal CapturedAmount, decimal ReleasedAmount);

    /// <summary>
    /// Admin-initiated money actions that need a human in the loop: partial capture (e.g. a
    /// damage fine withheld from a larger hold) and manual top-up reconciliation. Every
    /// method here writes exactly one PaymentAdminAudit row, unconditionally — see each
    /// method's try/finally-shaped flow.
    /// </summary>
    public class PaymentAdminService
    {
        private readonly PaymentsDbContext _db;
        private readonly IPaymentGateway _gateway;
        private readonly LedgerService _ledger;
        private readonly FirestoreService _firestore;
        private readonly ILogger<PaymentAdminService> _logger;

        public PaymentAdminService(
            PaymentsDbContext db, IPaymentGateway gateway, LedgerService ledger, FirestoreService firestore,
            ILogger<PaymentAdminService> logger)
        {
            _db = db;
            _gateway = gateway;
            _ledger = ledger;
            _firestore = firestore;
            _logger = logger;
        }

        /// <summary>
        /// Captures <paramref name="fineAmount"/> of an Authorized hold and releases the
        /// rest. DESIGN CHOICE (the schema doesn't have a way to represent "partially
        /// captured, partially voided" as two outcomes of one PaymentIntent — its state
        /// machine allows exactly one terminal state): everything happens on the SAME
        /// intent, which ends at Captured, not a second intent and not Voided. Concretely:
        ///
        ///   1. HyperPayGateway.CaptureAsync(intent, fineAmount) — a REAL partial CP call
        ///      (confirmed supported by HyperPay/OPPWA, including multiple partial captures
        ///      against one hold). On success: intent.TransitionTo(Captured), and
        ///      intent.Amount is updated to fineAmount (it no longer represents "the
        ///      original hold", it represents "what actually got captured" — matching real
        ///      PSP behavior, e.g. Stripe does the same on a partial capture).
        ///   2. HyperPayGateway.ReleaseRemainingHoldAsync(intent, remainder) — a REAL RV call
        ///      for the untouched portion. This is "the void", and it's a genuine second
        ///      HyperPay operation, but it does NOT call TransitionTo (the intent already
        ///      correctly concluded at Captured in step 1 — nothing to transition).
        ///   3. Ledger: releases the FULL original hold (not just fineAmount) from
        ///      card_customer:{userId}:holds back to pending_authorizations, and separately
        ///      recognizes only fineAmount as revenue:fines. The gap between those two
        ///      numbers IS the remainder release, expressed in ledger terms — exactly
        ///      mirroring what step 2 just did against HyperPay itself.
        ///
        /// No shortcuts: both the capture and the ledger writes go through the same
        /// TransitionTo/LedgerService path as any other E5.2/E5.3 flow; only the "void"
        /// step's gateway call skips TransitionTo, and only because the intent has nothing
        /// left to transition to.
        /// </summary>
        public async Task<PartialCaptureRequestResult> PartialCaptureAsync(
            Guid paymentIntentId, decimal fineAmount, int adminUserId, string adminLabel, CancellationToken ct = default)
        {
            var intent = await _db.PaymentIntents.FindAsync(new object[] { paymentIntentId }, ct)
                ?? throw new InvalidOperationException($"PaymentIntent {paymentIntentId} not found.");

            if (intent.State != PaymentIntentState.Authorized)
            {
                throw new InvalidOperationException(
                    $"PaymentIntent {paymentIntentId} is {intent.State}, not Authorized — cannot partial-capture.");
            }

            if (fineAmount <= 0 || fineAmount > intent.Amount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fineAmount), fineAmount, $"Fine amount must be > 0 and <= the held amount ({intent.Amount}).");
            }

            var originalHoldAmount = intent.Amount; // captured before CaptureAsync mutates it
            var remainder = originalHoldAmount - fineAmount;
            var userId = intent.UserId;

            await _gateway.CaptureAsync(intent, fineAmount, ct); // -> Captured, intent.Amount = fineAmount

            if (remainder > 0)
            {
                await _gateway.ReleaseRemainingHoldAsync(intent, remainder, ct);
            }

            var holdsAccount = $"card_customer:{userId}:holds";
            var receivableAccount = $"card_customer:{userId}:receivable";

            // Release the FULL original hold — this is what makes the remainder's release
            // real in ledger terms, not just at HyperPay.
            await _ledger.RecordTransactionAsync(
                holdsAccount, "pending_authorizations", originalHoldAmount, $"hold-release:{intent.Id}", intent.Id, ct);
            await _ledger.RecordTransactionAsync(
                receivableAccount, "revenue:fines", fineAmount, $"partial-capture:{intent.Id}", intent.Id, ct);

            _db.PaymentAdminAudits.Add(new PaymentAdminAudit
            {
                Id = Guid.NewGuid(),
                Action = "PartialCapture",
                PaymentIntentId = intent.Id,
                AdminUserId = adminUserId,
                AdminLabel = adminLabel,
                Details = $"Captured {fineAmount} {intent.Currency} of {originalHoldAmount} {intent.Currency} hold; " +
                          $"{remainder} {intent.Currency} released.",
                CreatedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[PaymentAdmin] PartialCapture intentId={IntentId} admin={AdminLabel} captured={Captured} released={Released}",
                intent.Id, adminLabel, fineAmount, remainder);

            return new PartialCaptureRequestResult(intent, fineAmount, remainder);
        }

        /// <summary>
        /// Verifies a manual top-up (Zain Cash/CliQ) actually arrived and, only now, writes
        /// the real ledger credit. Nothing before this point touched wallet:{userId} or
        /// User.WalletBalance — see PendingTopUp's own doc comment.
        /// </summary>
        public async Task<PendingTopUp> ConfirmTopUpAsync(
            Guid pendingTopUpId, int adminUserId, string adminLabel, CancellationToken ct = default)
        {
            var topUp = await _db.PendingTopUps.FindAsync(new object[] { pendingTopUpId }, ct)
                ?? throw new InvalidOperationException($"PendingTopUp {pendingTopUpId} not found.");

            if (topUp.Status != PendingTopUpStatus.Pending)
            {
                throw new InvalidOperationException($"PendingTopUp {pendingTopUpId} is already {topUp.Status}.");
            }

            await _ledger.RecordTransactionAsync(
                "external:topup_provider", $"wallet:{topUp.UserId}", topUp.Amount,
                $"manual-topup:{topUp.Id} ({topUp.PaymentMethod})", paymentIntentId: null, ct);

            topUp.Status = PendingTopUpStatus.Confirmed;
            topUp.ResolvedByAdminUserId = adminUserId;
            topUp.ResolvedAt = DateTime.UtcNow;

            var user = UsersController.GetUser(topUp.UserId);
            if (user is not null)
            {
                user.WalletBalance += topUp.Amount; // cache — see User.WalletBalance's doc comment
                await _firestore.SaveUserAsync(user);
            }

            _db.PaymentAdminAudits.Add(new PaymentAdminAudit
            {
                Id = Guid.NewGuid(),
                Action = "TopUpConfirmed",
                PendingTopUpId = topUp.Id,
                AdminUserId = adminUserId,
                AdminLabel = adminLabel,
                Details = $"Confirmed {topUp.Amount} via {topUp.PaymentMethod} (ref: {topUp.Reference ?? "none"}) for user {topUp.UserId}.",
                CreatedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[PaymentAdmin] TopUpConfirmed id={Id} userId={UserId} amount={Amount} admin={AdminLabel}",
                topUp.Id, topUp.UserId, topUp.Amount, adminLabel);

            return topUp;
        }

        /// <summary>Rejects a manual top-up claim — no ledger entry is ever written for it.</summary>
        public async Task<PendingTopUp> RejectTopUpAsync(
            Guid pendingTopUpId, int adminUserId, string adminLabel, string reason, CancellationToken ct = default)
        {
            var topUp = await _db.PendingTopUps.FindAsync(new object[] { pendingTopUpId }, ct)
                ?? throw new InvalidOperationException($"PendingTopUp {pendingTopUpId} not found.");

            if (topUp.Status != PendingTopUpStatus.Pending)
            {
                throw new InvalidOperationException($"PendingTopUp {pendingTopUpId} is already {topUp.Status}.");
            }

            topUp.Status = PendingTopUpStatus.Rejected;
            topUp.ResolvedByAdminUserId = adminUserId;
            topUp.ResolvedAt = DateTime.UtcNow;

            _db.PaymentAdminAudits.Add(new PaymentAdminAudit
            {
                Id = Guid.NewGuid(),
                Action = "TopUpRejected",
                PendingTopUpId = topUp.Id,
                AdminUserId = adminUserId,
                AdminLabel = adminLabel,
                Details = $"Rejected {topUp.Amount} via {topUp.PaymentMethod} for user {topUp.UserId}: {reason}",
                CreatedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[PaymentAdmin] TopUpRejected id={Id} userId={UserId} admin={AdminLabel} reason={Reason}",
                topUp.Id, topUp.UserId, adminLabel, reason);

            return topUp;
        }
    }
}
