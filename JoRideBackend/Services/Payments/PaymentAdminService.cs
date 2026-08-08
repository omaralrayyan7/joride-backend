using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Payments
{
    public record PartialCaptureRequestResult(PaymentIntent Intent, decimal CapturedAmount, decimal ReleasedAmount);

    /// <summary>
    /// One row of the weekly payout report. VehicleId is null for revenue that couldn't be
    /// traced back to a vehicle (no Trip linked to the PaymentIntent) — see
    /// PaymentAdminService.GeneratePayoutReportAsync for why that can happen and how it's
    /// surfaced rather than silently dropped.
    /// </summary>
    public record PayoutReportRow(
        int? VehicleId, string NameOrPlate, decimal GrossRevenue, decimal PlatformFeePercent, decimal PlatformFee, decimal NetPayout);

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
        private readonly IConfiguration _configuration;
        private readonly ILogger<PaymentAdminService> _logger;

        public PaymentAdminService(
            PaymentsDbContext db, IPaymentGateway gateway, LedgerService ledger, FirestoreService firestore,
            IConfiguration configuration, ILogger<PaymentAdminService> logger)
        {
            _db = db;
            _gateway = gateway;
            _ledger = ledger;
            _firestore = firestore;
            _configuration = configuration;
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

        /// <summary>
        /// Read-only weekly payout report: for each vehicle, sums the revenue actually
        /// recognized (revenue:trips from full captures, revenue:fines from partial
        /// captures — see E5.3/E5.4) within [periodStart, periodEnd). Writes NO ledger
        /// entries and marks nothing as paid — payouts are manual bank/CliQ transfers at
        /// launch; "mark as paid" is future work.
        ///
        /// GROUPING: there is no owner/partner concept anywhere in this codebase yet
        /// (Vehicle has no OwnerId or similar) — grouping is per-vehicle instead, as a
        /// documented placeholder for real owner grouping once that concept exists.
        ///
        /// Revenue entries carry a PaymentIntentId but not a VehicleId directly — the trail
        /// is LedgerEntry -> PaymentIntent.TripId -> Trip.VehicleId. Trip lives in Firestore
        /// (via the in-memory TripsController), not Postgres, so that last hop is resolved
        /// in memory rather than a SQL join. A PaymentIntent with no TripId (or a TripId
        /// that doesn't resolve to a live trip) can't be traced to a vehicle; rather than
        /// silently dropping that revenue, it's reported under a single explicit
        /// "Unassigned" row so the total always reconciles to the real ledger sum.
        ///
        /// PLATFORM FEE: no fee percentage is configured anywhere in this codebase
        /// (Payouts:PlatformFeePercent) — this is a real, unmade business decision, not an
        /// oversight. Defaults to 0% until product/finance sets it.
        /// </summary>
        public async Task<IReadOnlyList<PayoutReportRow>> GeneratePayoutReportAsync(
            DateTime periodStart, DateTime periodEnd, int adminUserId, string adminLabel, CancellationToken ct = default)
        {
            if (periodEnd <= periodStart)
            {
                throw new ArgumentException("periodEnd must be after periodStart.");
            }

            var feePercent = _configuration.GetValue<decimal?>("Payouts:PlatformFeePercent") ?? 0m;

            var revenueEntries = await (
                from e in _db.LedgerEntries
                join p in _db.PaymentIntents on e.PaymentIntentId equals p.Id
                where (e.CreditAccount == "revenue:trips" || e.CreditAccount == "revenue:fines")
                      && e.CreatedAt >= periodStart && e.CreatedAt < periodEnd
                select new { e.Amount, p.TripId }
            ).ToListAsync(ct);

            const int UnassignedVehicleId = 0; // sentinel: no vehicle could be resolved
            var grossByVehicle = new Dictionary<int, decimal>();
            foreach (var entry in revenueEntries)
            {
                var vehicleId = UnassignedVehicleId;
                if (entry.TripId is int tripId)
                {
                    var trip = TripsController.GetTrip(tripId);
                    if (trip is not null)
                    {
                        vehicleId = trip.VehicleId;
                    }
                }

                grossByVehicle[vehicleId] = grossByVehicle.GetValueOrDefault(vehicleId) + entry.Amount;
            }

            var rows = new List<PayoutReportRow>();
            foreach (var (vehicleId, gross) in grossByVehicle.OrderBy(kv => kv.Key))
            {
                var isUnassigned = vehicleId == UnassignedVehicleId;
                var nameOrPlate = isUnassigned
                    ? "Unassigned (no linked trip)"
                    : VehiclesController.GetVehicleById(vehicleId)?.LicensePlate ?? $"Vehicle #{vehicleId}";
                var fee = Math.Round(gross * feePercent / 100m, 2);

                rows.Add(new PayoutReportRow(
                    isUnassigned ? null : vehicleId, nameOrPlate, gross, feePercent, fee, gross - fee));
            }

            // Audit only — this method never touches LedgerEntries or marks anything paid.
            _db.PaymentAdminAudits.Add(new PaymentAdminAudit
            {
                Id = Guid.NewGuid(),
                Action = "PayoutReportGenerated",
                AdminUserId = adminUserId,
                AdminLabel = adminLabel,
                Details = $"Period {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}: {rows.Count} vehicle row(s), " +
                          $"total gross {rows.Sum(r => r.GrossRevenue):F2}.",
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[PaymentAdmin] PayoutReportGenerated admin={AdminLabel} period={PeriodStart:o}..{PeriodEnd:o} rows={RowCount}",
                adminLabel, periodStart, periodEnd, rows.Count);

            return rows;
        }
    }
}
