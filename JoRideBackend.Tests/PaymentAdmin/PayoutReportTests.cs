using JoRideBackend.Data;
using JoRideBackend.Models;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services;
using JoRideBackend.Services.Payments;
using JoRideBackend.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.PaymentAdmin;

/// <summary>
/// TripsController/VehiclesController hold process-wide static state (see CLAUDE.md) — this
/// is the only test file that seeds it, via Initialize (which fully replaces the list each
/// call). Not collection-isolated from other test classes; safe today because no other test
/// file touches these two controllers' statics.
/// </summary>
public class PayoutReportTests
{
    private static (PaymentsDbContext Db, PaymentAdminService Service) CreateSut(decimal? platformFeePercent = null)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);
        var gateway = new FakePaymentGateway();
        var ledger = new LedgerService(db, NullLogger<LedgerService>.Instance);
        var firestore = new FirestoreService(new ConfigurationBuilder().Build(), NullLogger<FirestoreService>.Instance);
        var configValues = new Dictionary<string, string?>();
        if (platformFeePercent is not null)
        {
            configValues["Payouts:PlatformFeePercent"] = platformFeePercent.Value.ToString();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var service = new PaymentAdminService(db, gateway, ledger, firestore, configuration, NullLogger<PaymentAdminService>.Instance);
        return (db, service);
    }

    private static PaymentIntent SeedCapturedIntent(PaymentsDbContext db, int? tripId, decimal revenueAmount, DateTime createdAt, bool asFine = false)
    {
        var intent = new PaymentIntent
        {
            Id = Guid.NewGuid(),
            Amount = revenueAmount,
            Currency = "USD",
            UserId = 1,
            TripId = tripId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        intent.TransitionTo(PaymentIntentState.Authorized);
        intent.TransitionTo(PaymentIntentState.Captured);
        intent.ProviderRef = $"/v1/payments/{intent.Id:N}";
        db.PaymentIntents.Add(intent);

        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.NewGuid(),
            PaymentIntentId = intent.Id,
            DebitAccount = $"card_customer:1:receivable",
            CreditAccount = asFine ? "revenue:fines" : "revenue:trips",
            Amount = revenueAmount,
            Reference = $"capture:{intent.Id}",
            CreatedAt = createdAt,
        });
        db.SaveChanges();
        return intent;
    }

    [Fact]
    public async Task Groups_revenue_per_vehicle_via_TripId()
    {
        VehiclesController.Initialize(new List<Vehicle>
        {
            new() { Id = 9001, LicensePlate = "PR-TEST-1" },
            new() { Id = 9002, LicensePlate = "PR-TEST-2" },
        }, null!);
        TripsController.Initialize(new List<Trip>
        {
            new() { Id = 9101, VehicleId = 9001, UserId = 1 },
            new() { Id = 9102, VehicleId = 9002, UserId = 1 },
        }, null!);

        var (db, service) = CreateSut();
        var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var mid = periodStart.AddDays(2);

        SeedCapturedIntent(db, tripId: 9101, revenueAmount: 40m, createdAt: mid);
        SeedCapturedIntent(db, tripId: 9101, revenueAmount: 10m, createdAt: mid, asFine: true);
        SeedCapturedIntent(db, tripId: 9102, revenueAmount: 25m, createdAt: mid);

        var rows = await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 15, adminLabel: "Admin: Test (#15)");

        var vehicle1Row = Assert.Single(rows, r => r.VehicleId == 9001);
        Assert.Equal("PR-TEST-1", vehicle1Row.NameOrPlate);
        Assert.Equal(50m, vehicle1Row.GrossRevenue); // 40 (trip) + 10 (fine)

        var vehicle2Row = Assert.Single(rows, r => r.VehicleId == 9002);
        Assert.Equal(25m, vehicle2Row.GrossRevenue);
    }

    [Fact]
    public async Task Revenue_with_no_linked_trip_is_reported_as_Unassigned_not_dropped()
    {
        var (db, service) = CreateSut();
        var periodStart = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 2, 8, 0, 0, 0, DateTimeKind.Utc);

        SeedCapturedIntent(db, tripId: null, revenueAmount: 33m, createdAt: periodStart.AddHours(1));

        var rows = await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 15, adminLabel: "Admin");

        var unassigned = Assert.Single(rows, r => r.VehicleId == null);
        Assert.Equal(33m, unassigned.GrossRevenue);
        Assert.Contains("Unassigned", unassigned.NameOrPlate);
    }

    [Fact]
    public async Task Entries_outside_the_period_are_excluded()
    {
        VehiclesController.Initialize(new List<Vehicle> { new() { Id = 9003, LicensePlate = "PR-TEST-3" } }, null!);
        TripsController.Initialize(new List<Trip> { new() { Id = 9103, VehicleId = 9003, UserId = 1 } }, null!);

        var (db, service) = CreateSut();
        var periodStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 3, 8, 0, 0, 0, DateTimeKind.Utc);

        SeedCapturedIntent(db, tripId: 9103, revenueAmount: 999m, createdAt: periodStart.AddDays(-1)); // before
        SeedCapturedIntent(db, tripId: 9103, revenueAmount: 5m, createdAt: periodStart.AddDays(1));    // inside
        SeedCapturedIntent(db, tripId: 9103, revenueAmount: 999m, createdAt: periodEnd);               // at/after end (exclusive)

        var rows = await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 15, adminLabel: "Admin");

        var row = Assert.Single(rows);
        Assert.Equal(5m, row.GrossRevenue);
    }

    [Fact]
    public async Task Platform_fee_defaults_to_zero_when_unconfigured()
    {
        var (db, service) = CreateSut(platformFeePercent: null);
        var periodStart = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc);
        SeedCapturedIntent(db, tripId: null, revenueAmount: 100m, createdAt: periodStart.AddHours(1));

        var rows = await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 15, adminLabel: "Admin");

        var row = Assert.Single(rows);
        Assert.Equal(0m, row.PlatformFeePercent);
        Assert.Equal(0m, row.PlatformFee);
        Assert.Equal(100m, row.NetPayout);
    }

    [Fact]
    public async Task Platform_fee_applies_when_configured()
    {
        var (db, service) = CreateSut(platformFeePercent: 20m);
        var periodStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc);
        SeedCapturedIntent(db, tripId: null, revenueAmount: 100m, createdAt: periodStart.AddHours(1));

        var rows = await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 15, adminLabel: "Admin");

        var row = Assert.Single(rows);
        Assert.Equal(20m, row.PlatformFee);
        Assert.Equal(80m, row.NetPayout);
    }

    [Fact]
    public async Task Report_writes_an_audit_row_naming_admin_and_period_but_no_ledger_entries()
    {
        var (db, service) = CreateSut();
        var periodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
        SeedCapturedIntent(db, tripId: null, revenueAmount: 10m, createdAt: periodStart.AddHours(1));

        var ledgerCountBefore = await db.LedgerEntries.CountAsync();

        await service.GeneratePayoutReportAsync(periodStart, periodEnd, adminUserId: 42, adminLabel: "Admin: Reporter (#42)");

        var ledgerCountAfter = await db.LedgerEntries.CountAsync();
        Assert.Equal(ledgerCountBefore, ledgerCountAfter); // read-only: no new ledger entries

        var audit = Assert.Single(await db.PaymentAdminAudits.Where(a => a.Action == "PayoutReportGenerated").ToListAsync());
        Assert.Equal(42, audit.AdminUserId);
        Assert.Equal("Admin: Reporter (#42)", audit.AdminLabel);
        Assert.Contains("2026-06-01", audit.Details);
        Assert.Contains("2026-06-08", audit.Details);
    }

    [Fact]
    public async Task Rejects_a_period_where_end_is_not_after_start()
    {
        var (_, service) = CreateSut();
        var same = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GeneratePayoutReportAsync(same, same, adminUserId: 1, adminLabel: "Admin"));
    }
}
