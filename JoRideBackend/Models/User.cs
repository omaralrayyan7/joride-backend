namespace JoRideBackend.Models
{
    public enum KycStatus
    {
        Pending,
        Approved,
        Rejected,
    }

    public class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? PasswordHash { get; set; }
        public string? Phone { get; set; }
        public string? IdNumber { get; set; }
        public string? DrivingLicenseNumber { get; set; }
        public bool IsActive { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsLicenseVerified { get; set; }
        public bool IsEmailVerified { get; set; }
        public bool IsPhoneVerified { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// CACHE, not source of truth. The real balance is SUM(ledger entries for
        /// "wallet:{Id}") in the Postgres double-entry ledger (see LedgerService,
        /// PaymentsDbContext.LedgerEntries) — GET /api/wallet computes it from there.
        /// This field is kept in sync as a convenience for the other places that still read
        /// it directly (AuthResponse/AuthUser, TripsController's own balance checks, etc.)
        /// without a round-trip to Postgres. Read-only everywhere except
        /// WalletController.TryChargeAsync/RefundAsync/RecordPayment/TopUp — those are the
        /// only writers, and each one writes the matching ledger entry in the same breath.
        /// Don't add another write path for this field without also writing to the ledger.
        /// </summary>
        public decimal WalletBalance { get; set; } = 0m;
        public string? ProfileImageUrl { get; set; }

        // ── Brute-force / lockout ──────────────────────────────────────────────
        public int FailedLoginAttempts { get; set; } = 0;
        public DateTime? LockoutEndUtc { get; set; }

        /// <summary>
        /// Defaults to Pending for every new registration — nothing sets it to Approved
        /// except KycAdminController's approve endpoint (admin-only, reason required,
        /// audited). Deliberately NOT one of the fields UsersController.Update copies from
        /// its request body — that endpoint is reachable by any authenticated user for
        /// their own profile, so a settable KycStatus there would make the "KycApproved"
        /// booking gate trivially self-approvable. Existing Firestore documents that
        /// predate this field read back as Pending (see FirestoreService.DocToUser).
        /// </summary>
        public KycStatus KycStatus { get; set; } = KycStatus.Pending;
    }
}
