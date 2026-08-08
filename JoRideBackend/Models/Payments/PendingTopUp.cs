namespace JoRideBackend.Models.Payments
{
    public enum PendingTopUpStatus
    {
        /// <summary>Submitted by the user, not yet reconciled against the manual payment
        /// rail (Zain Cash/CliQ) — no ledger credit exists for it yet.</summary>
        Pending,

        /// <summary>An admin verified the manual payment actually arrived; the ledger credit
        /// was written at that moment (see PaymentAdminService.ConfirmTopUpAsync).</summary>
        Confirmed,

        /// <summary>An admin could not verify the payment (e.g. no matching transfer found);
        /// no ledger credit was ever written.</summary>
        Rejected,
    }

    /// <summary>
    /// A user-submitted top-up via a manual-reconciliation payment rail (Zain Cash, CliQ) —
    /// these aren't automatically confirmed the way a HyperPay card checkout is, so the
    /// ledger credit only happens once an admin verifies the money actually arrived.
    /// "Ledger-adjacent": this row itself is never summed into any account balance: it's a
    /// worklist entry, not a value carrier. wallet:{userId} only gains balance via the
    /// LedgerEntry that PaymentAdminService.ConfirmTopUpAsync writes once, on confirmation.
    /// </summary>
    public class PendingTopUp
    {
        public Guid Id { get; set; }
        public int UserId { get; set; }
        public decimal Amount { get; set; }
        public string PaymentMethod { get; set; } = string.Empty; // "Zain Cash", "CliQ", ...
        public string? Reference { get; set; } // e.g. the transfer reference the user provides
        public PendingTopUpStatus Status { get; set; } = PendingTopUpStatus.Pending;
        public int? ResolvedByAdminUserId { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
