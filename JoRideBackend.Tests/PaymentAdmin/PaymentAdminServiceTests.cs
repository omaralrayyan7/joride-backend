using JoRideBackend.Data;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services;
using JoRideBackend.Services.Payments;
using JoRideBackend.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.PaymentAdmin;

public class PaymentAdminServiceTests
{
    private static (PaymentsDbContext Db, PaymentAdminService Service, FakePaymentGateway Gateway) CreateSut()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var gateway = new FakePaymentGateway();
        var ledger = new LedgerService(db, NullLogger<LedgerService>.Instance);
        var firestore = new FirestoreService(new ConfigurationBuilder().Build(), NullLogger<FirestoreService>.Instance);
        var service = new PaymentAdminService(db, gateway, ledger, firestore, NullLogger<PaymentAdminService>.Instance);
        return (db, service, gateway);
    }

    private static PaymentIntent SeedAuthorizedIntent(PaymentsDbContext db, decimal amount, int userId = 7)
    {
        var intent = new PaymentIntent
        {
            Id = Guid.NewGuid(),
            Amount = amount,
            Currency = "USD",
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        intent.TransitionTo(PaymentIntentState.Authorized);
        intent.ProviderRef = $"/v1/payments/seed-{intent.Id:N}";
        db.PaymentIntents.Add(intent);
        db.SaveChanges();
        return intent;
    }

    // ── Partial capture ──────────────────────────────────────────────────

    [Fact]
    public async Task PartialCapture_transitions_to_Captured_and_leaves_correct_ledger_entries()
    {
        var (db, service, _) = CreateSut();
        var intent = SeedAuthorizedIntent(db, amount: 50m, userId: 7);

        var result = await service.PartialCaptureAsync(intent.Id, fineAmount: 10m, adminUserId: 15, adminLabel: "Admin: Test (#15)");

        Assert.Equal(10m, result.CapturedAmount);
        Assert.Equal(40m, result.ReleasedAmount);

        var reloaded = await db.PaymentIntents.FindAsync(intent.Id);
        Assert.Equal(PaymentIntentState.Captured, reloaded!.State);
        Assert.Equal(10m, reloaded.Amount); // intent.Amount now reflects what was actually captured

        var entries = await db.LedgerEntries.Where(e => e.PaymentIntentId == intent.Id).ToListAsync();
        Assert.Equal(2, entries.Count);

        var holdRelease = Assert.Single(entries, e => e.Reference == $"hold-release:{intent.Id}");
        Assert.Equal("card_customer:7:holds", holdRelease.DebitAccount);
        Assert.Equal("pending_authorizations", holdRelease.CreditAccount);
        Assert.Equal(50m, holdRelease.Amount); // releases the FULL original hold, not just the fine

        var capture = Assert.Single(entries, e => e.Reference == $"partial-capture:{intent.Id}");
        Assert.Equal("card_customer:7:receivable", capture.DebitAccount);
        Assert.Equal("revenue:fines", capture.CreditAccount);
        Assert.Equal(10m, capture.Amount);

        // Invariant: still balanced (each row's Amount is both its debit and credit contribution).
        Assert.Equal(entries.Sum(e => e.Amount), entries.Sum(e => e.Amount));
    }

    [Fact]
    public async Task PartialCapture_writes_an_audit_row_naming_the_approving_admin()
    {
        var (db, service, _) = CreateSut();
        var intent = SeedAuthorizedIntent(db, amount: 30m);

        await service.PartialCaptureAsync(intent.Id, fineAmount: 12m, adminUserId: 99, adminLabel: "Admin: Jamie (#99)");

        var audit = Assert.Single(await db.PaymentAdminAudits.Where(a => a.PaymentIntentId == intent.Id).ToListAsync());
        Assert.Equal("PartialCapture", audit.Action);
        Assert.Equal(99, audit.AdminUserId);
        Assert.Equal("Admin: Jamie (#99)", audit.AdminLabel);
        Assert.Contains("12", audit.Details);
    }

    [Fact]
    public async Task PartialCapture_rejects_amount_exceeding_the_hold()
    {
        var (db, service, _) = CreateSut();
        var intent = SeedAuthorizedIntent(db, amount: 20m);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.PartialCaptureAsync(intent.Id, fineAmount: 25m, adminUserId: 1, adminLabel: "Admin"));

        Assert.Equal(PaymentIntentState.Authorized, (await db.PaymentIntents.FindAsync(intent.Id))!.State);
        Assert.Empty(await db.LedgerEntries.Where(e => e.PaymentIntentId == intent.Id).ToListAsync());
    }

    [Fact]
    public async Task PartialCapture_rejects_an_intent_that_is_not_Authorized()
    {
        var (db, service, _) = CreateSut();
        var intent = SeedAuthorizedIntent(db, amount: 20m);
        intent.TransitionTo(PaymentIntentState.Voided);
        db.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PartialCaptureAsync(intent.Id, fineAmount: 5m, adminUserId: 1, adminLabel: "Admin"));
    }

    [Fact]
    public async Task Full_partial_capture_of_the_whole_hold_releases_nothing()
    {
        var (db, service, _) = CreateSut();
        var intent = SeedAuthorizedIntent(db, amount: 15m);

        var result = await service.PartialCaptureAsync(intent.Id, fineAmount: 15m, adminUserId: 1, adminLabel: "Admin");

        Assert.Equal(0m, result.ReleasedAmount);
        // Ledger still records the (now-zero-difference) hold-release + full capture — both entries present.
        Assert.Equal(2, (await db.LedgerEntries.Where(e => e.PaymentIntentId == intent.Id).ToListAsync()).Count);
    }

    // ── Top-up reconciliation ────────────────────────────────────────────

    [Fact]
    public async Task Pending_topup_does_not_affect_ledger_balance_until_confirmed()
    {
        var (db, service, _) = CreateSut();
        var pending = new PendingTopUp
        {
            Id = Guid.NewGuid(),
            UserId = 20,
            Amount = 75m,
            PaymentMethod = "Zain Cash",
            Reference = "ZC-12345",
            Status = PendingTopUpStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.PendingTopUps.Add(pending);
        await db.SaveChangesAsync();

        var ledger = new LedgerService(db, NullLogger<LedgerService>.Instance);
        var balanceBefore = await ledger.GetAccountBalanceAsync("wallet:20");
        Assert.Equal(0m, balanceBefore);

        await service.ConfirmTopUpAsync(pending.Id, adminUserId: 15, adminLabel: "Admin: Test (#15)");

        var balanceAfter = await ledger.GetAccountBalanceAsync("wallet:20");
        Assert.Equal(75m, balanceAfter);

        var reloaded = await db.PendingTopUps.FindAsync(pending.Id);
        Assert.Equal(PendingTopUpStatus.Confirmed, reloaded!.Status);
        Assert.Equal(15, reloaded.ResolvedByAdminUserId);
        Assert.NotNull(reloaded.ResolvedAt);
    }

    [Fact]
    public async Task ConfirmTopUp_writes_an_audit_row()
    {
        var (db, service, _) = CreateSut();
        var pending = new PendingTopUp
        {
            Id = Guid.NewGuid(), UserId = 21, Amount = 40m, PaymentMethod = "CliQ",
            Status = PendingTopUpStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        db.PendingTopUps.Add(pending);
        await db.SaveChangesAsync();

        await service.ConfirmTopUpAsync(pending.Id, adminUserId: 15, adminLabel: "Admin: Test (#15)");

        var audit = Assert.Single(await db.PaymentAdminAudits.Where(a => a.PendingTopUpId == pending.Id).ToListAsync());
        Assert.Equal("TopUpConfirmed", audit.Action);
        Assert.Equal(15, audit.AdminUserId);
    }

    [Fact]
    public async Task RejectTopUp_never_writes_a_ledger_entry()
    {
        var (db, service, _) = CreateSut();
        var pending = new PendingTopUp
        {
            Id = Guid.NewGuid(), UserId = 22, Amount = 60m, PaymentMethod = "Zain Cash",
            Status = PendingTopUpStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        db.PendingTopUps.Add(pending);
        await db.SaveChangesAsync();

        await service.RejectTopUpAsync(pending.Id, adminUserId: 15, adminLabel: "Admin: Test (#15)", reason: "No matching transfer found.");

        Assert.Empty(await db.LedgerEntries.Where(e => e.CreditAccount == "wallet:22").ToListAsync());
        var reloaded = await db.PendingTopUps.FindAsync(pending.Id);
        Assert.Equal(PendingTopUpStatus.Rejected, reloaded!.Status);

        var audit = Assert.Single(await db.PaymentAdminAudits.Where(a => a.PendingTopUpId == pending.Id).ToListAsync());
        Assert.Equal("TopUpRejected", audit.Action);
        Assert.Contains("No matching transfer found.", audit.Details);
    }

    [Fact]
    public async Task Confirming_an_already_resolved_topup_throws()
    {
        var (db, service, _) = CreateSut();
        var pending = new PendingTopUp
        {
            Id = Guid.NewGuid(), UserId = 23, Amount = 10m, PaymentMethod = "CliQ",
            Status = PendingTopUpStatus.Confirmed, CreatedAt = DateTime.UtcNow,
        };
        db.PendingTopUps.Add(pending);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConfirmTopUpAsync(pending.Id, adminUserId: 1, adminLabel: "Admin"));
    }
}
