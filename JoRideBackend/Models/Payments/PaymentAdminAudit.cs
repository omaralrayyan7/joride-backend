namespace JoRideBackend.Models.Payments
{
    /// <summary>
    /// Immutable audit trail for admin-initiated money actions (partial capture, top-up
    /// confirm/reject). Payments-specific counterpart to CommandAudit (which is FK'd to
    /// DeviceCommand and doesn't fit money actions) — same idea: every admin action here
    /// writes exactly one row, unconditionally, so it's always traceable to who approved it
    /// and when.
    /// </summary>
    public class PaymentAdminAudit
    {
        public Guid Id { get; set; }
        public string Action { get; set; } = string.Empty; // "PartialCapture", "TopUpConfirmed", "TopUpRejected"
        public Guid? PaymentIntentId { get; set; }
        public Guid? PendingTopUpId { get; set; }
        public int AdminUserId { get; set; }
        public string AdminLabel { get; set; } = string.Empty; // e.g. "Admin: Administrator (#15)"
        public string Details { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
