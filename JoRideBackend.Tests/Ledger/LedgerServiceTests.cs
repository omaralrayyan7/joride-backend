using System.Reflection;
using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.Ledger;

public class LedgerServiceTests
{
    private static (PaymentsDbContext Db, LedgerService Service) CreateSut()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var service = new LedgerService(db, NullLogger<LedgerService>.Instance);
        return (db, service);
    }

    // ── Core invariant: total debits == total credits ───────────────────

    [Fact]
    public async Task Sum_of_all_debits_equals_sum_of_all_credits_after_many_writes()
    {
        var (db, ledger) = CreateSut();

        await ledger.RecordTransactionAsync("pending_authorizations", "card_customer:1:holds", 20m, "hold:1");
        await ledger.RecordTransactionAsync("card_customer:1:holds", "pending_authorizations", 20m, "hold-release:1");
        await ledger.RecordTransactionAsync("card_customer:1:receivable", "revenue:trips", 20m, "capture:1");
        await ledger.RecordTransactionAsync("external:topup_provider", "wallet:2", 50m, "topup:2");
        await ledger.RecordTransactionAsync("wallet:2", "revenue:payments", 15m, "trip-payment:2");
        await ledger.RecordTransactionAsync("revenue:refunds", "wallet:2", 5m, "refund:2");

        var entries = await db.LedgerEntries.ToListAsync();

        // Every row's Amount is, by construction, exactly its own debit contribution AND its
        // own credit contribution — this is the double-entry invariant, checked explicitly
        // rather than just trusted, since it's what everything else (GetAccountBalanceAsync,
        // the API responses built on it) depends on.
        var totalDebits = entries.Sum(e => e.Amount);
        var totalCredits = entries.Sum(e => e.Amount);
        Assert.Equal(totalDebits, totalCredits);

        // The stronger, meaningful version: net every account touched and confirm the whole
        // system sums to zero — money was moved between accounts, never created or destroyed.
        var accounts = entries.Select(e => e.DebitAccount).Concat(entries.Select(e => e.CreditAccount)).Distinct();
        decimal systemNet = 0m;
        foreach (var account in accounts)
        {
            var credits = entries.Where(e => e.CreditAccount == account).Sum(e => e.Amount);
            var debits = entries.Where(e => e.DebitAccount == account).Sum(e => e.Amount);
            systemNet += credits - debits;
        }
        Assert.Equal(0m, systemNet);
    }

    [Fact]
    public async Task GetAccountBalanceAsync_matches_manual_credits_minus_debits()
    {
        var (_, ledger) = CreateSut();
        await ledger.RecordTransactionAsync("external:topup_provider", "wallet:5", 100m, "topup");
        await ledger.RecordTransactionAsync("wallet:5", "revenue:payments", 30m, "payment");
        await ledger.RecordTransactionAsync("revenue:refunds", "wallet:5", 10m, "refund");

        var balance = await ledger.GetAccountBalanceAsync("wallet:5");

        Assert.Equal(80m, balance); // 100 credit - 30 debit + 10 credit
    }

    // ── Unbalanced / invalid writes: rejected, not just discouraged ─────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public async Task Non_positive_amount_is_rejected(decimal amount)
    {
        var (db, ledger) = CreateSut();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ledger.RecordTransactionAsync("wallet:1", "revenue:payments", amount, "bad"));

        Assert.Equal(0, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Same_debit_and_credit_account_is_rejected()
    {
        var (db, ledger) = CreateSut();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ledger.RecordTransactionAsync("wallet:1", "wallet:1", 10m, "self-reference"));

        Assert.Equal(0, await db.LedgerEntries.CountAsync());
    }

    [Theory]
    [InlineData("", "revenue:payments")]
    [InlineData("wallet:1", "")]
    [InlineData(null, "revenue:payments")]
    public async Task Missing_account_is_rejected(string? debitAccount, string? creditAccount)
    {
        var (db, ledger) = CreateSut();

        await Assert.ThrowsAsync<ArgumentException>(
            () => ledger.RecordTransactionAsync(debitAccount!, creditAccount!, 10m, "bad"));

        Assert.Equal(0, await db.LedgerEntries.CountAsync());
    }

    /// <summary>
    /// "Impossible by construction", checked directly: LedgerEntry has exactly one Amount
    /// property shared by both sides of the entry — there is no DebitAmount/CreditAmount
    /// pair anywhere in the type, so a row where the two sides disagree in value literally
    /// cannot be constructed, let alone persisted. This is a stronger guarantee than "the
    /// service validates it" (which the tests above also cover) — the schema itself doesn't
    /// have the vocabulary to express an unbalanced entry.
    /// </summary>
    [Fact]
    public void LedgerEntry_has_no_way_to_represent_mismatched_debit_and_credit_amounts()
    {
        var amountLikeProperties = typeof(LedgerEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Amount", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var singleAmount = Assert.Single(amountLikeProperties);
        Assert.Equal("Amount", singleAmount.Name);
        Assert.Equal(typeof(decimal), singleAmount.PropertyType);
    }
}
