using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Services.Payments
{
    /// <summary>
    /// Double-entry ledger. Every transaction is a single LedgerEntry row carrying both a
    /// DebitAccount and a CreditAccount against one shared Amount — there is no separate
    /// DebitAmount/CreditAmount pair anywhere in the schema, so a row where the two sides
    /// disagree in value is not just rejected, it's unrepresentable: the type doesn't have
    /// the fields to say it. Writing one row is a single INSERT, which SaveChangesAsync
    /// already wraps in an implicit database transaction — so a row's two sides can never
    /// be observed half-written.
    /// </summary>
    public class LedgerService
    {
        private readonly PaymentsDbContext _db;
        private readonly ILogger<LedgerService> _logger;

        public LedgerService(PaymentsDbContext db, ILogger<LedgerService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<LedgerEntry> RecordTransactionAsync(
            string debitAccount,
            string creditAccount,
            decimal amount,
            string reference,
            Guid? paymentIntentId = null,
            CancellationToken ct = default)
        {
            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Ledger amount must be positive.");
            }

            if (string.IsNullOrWhiteSpace(debitAccount) || string.IsNullOrWhiteSpace(creditAccount))
            {
                throw new ArgumentException("Debit and credit accounts are required.");
            }

            if (string.Equals(debitAccount, creditAccount, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Debit and credit accounts must differ (both were \"{debitAccount}\") — a self-referencing entry moves no real money.");
            }

            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                PaymentIntentId = paymentIntentId,
                DebitAccount = debitAccount,
                CreditAccount = creditAccount,
                Amount = amount,
                Reference = reference,
                CreatedAt = DateTime.UtcNow,
            };

            _db.LedgerEntries.Add(entry);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[Ledger] {Amount} {Debit} -> {Credit} ref={Reference} paymentIntentId={PaymentIntentId}",
                amount, debitAccount, creditAccount, reference, paymentIntentId);

            return entry;
        }

        /// <summary>Balance of one account: SUM(credits to it) - SUM(debits from it).</summary>
        public async Task<decimal> GetAccountBalanceAsync(string account, CancellationToken ct = default)
        {
            var credits = await _db.LedgerEntries
                .Where(e => e.CreditAccount == account)
                .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
            var debits = await _db.LedgerEntries
                .Where(e => e.DebitAccount == account)
                .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

            return credits - debits;
        }
    }
}
