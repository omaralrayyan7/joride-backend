using System.Security.Claims;
using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JoRideBackend.Tests.Trips;

/// <summary>
/// E3.2: exercises TripsController.Cancel's state-machine guard and ownership check directly
/// against the controller (no HTTP host). WalletController's static _scopeFactory is left
/// unwired here — RecordWalletLedgerEntryAsync's existing null-check fallback (see
/// WalletController.cs) makes the refund a harmless cache-only no-op, which is exactly right
/// for this test's purpose: it's about the state machine and authorization guard, not the
/// ledger — the ledger write itself (via WalletController.RefundAsync's real Postgres path)
/// is proven separately in DoubleBookingRaceTests' sibling and by live verification, since
/// hosting a real Postgres connection through TestServer for this kind of rapid, sequential
/// test run hits an unrelated Npgsql/TestServer connection-pooling flakiness.
/// </summary>
public class CancellationEndpointTests
{
    private static int _seed = 800000;

    private static (User Owner, Vehicle Vehicle) SeedFixture()
    {
        // If DoubleBookingRaceTests (a WebApplicationFactory-based test in the same process)
        // ran before this and its factory has since been torn down, WalletController's
        // static _scopeFactory would still point at that disposed DI container. Reset it so
        // RefundAsync/TryChargeAsync fall back to their intended cache-only no-op here (see
        // WalletController.RecordWalletLedgerEntryAsync) instead of throwing
        // ObjectDisposedException against a dead container.
        WalletController.SetServiceScopeFactory(null!);

        var id = System.Threading.Interlocked.Increment(ref _seed);
        var firestore = new FirestoreService(NullConfig(), NullLogger());

        var owner = new User { Id = id, Name = $"User {id}", Email = $"u{id}@test.local", IsActive = true, KycStatus = KycStatus.Approved, WalletBalance = 1000m };
        UsersController.Initialize(new List<User> { owner }, firestore);

        var vehicle = new Vehicle { Id = id, LicensePlate = $"T-{id}", Category = "Economy", Status = "Available", IsVisible = true };
        VehiclesController.Initialize(new List<Vehicle> { vehicle }, firestore);

        TripsController.Initialize(new List<Trip>(), firestore);

        return (owner, vehicle);
    }

    private static (TripsController Controller, User Owner, Vehicle Vehicle) CreateController(bool callerIsOwner = true, bool callerIsAdmin = false)
    {
        var (owner, vehicle) = SeedFixture();
        var callerId = callerIsOwner ? owner.Id : owner.Id + 500000;
        return (ControllerAs(callerId, callerIsAdmin), owner, vehicle);
    }

    /// <summary>A fresh TripsController instance impersonating the given caller — state is
    /// process-static, so this shares the same trips/users/vehicles as any other instance.</summary>
    private static TripsController ControllerAs(int callerId, bool isAdmin = false)
    {
        var claims = new List<Claim> { new("sub", callerId.ToString()) };
        if (isAdmin) claims.Add(new Claim("role", "admin"));

        return new TripsController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }

    private static Microsoft.Extensions.Configuration.IConfiguration NullConfig() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

    private static Microsoft.Extensions.Logging.ILogger<FirestoreService> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance;

    private static async Task<Trip> BookAsync(TripsController controller, int userId, int vehicleId, int duration = 4, string durationType = "hour", decimal totalFare = 40m)
    {
        var request = new StartTripRequest(userId, vehicleId, duration, durationType, totalFare, 0m, 0m, totalFare, "Credit Card");
        var result = await controller.Start(request);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        return Assert.IsType<Trip>(created.Value);
    }

    [Fact]
    public async Task Cancelling_a_freshly_booked_trip_succeeds_and_marks_it_cancelled()
    {
        var (controller, owner, vehicle) = CreateController();
        var trip = await BookAsync(controller, owner.Id, vehicle.Id, duration: 2, durationType: "day");

        var result = await controller.Cancel(trip.Id, new CancelTripRequest("changed my mind"));

        var ok = Assert.IsType<ActionResult<Trip>>(result);
        var cancelled = Assert.IsType<Trip>(ok.Value);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.EndTime);
        Assert.Equal(0m, cancelled.TotalFare); // free tier: fully refunded
        Assert.Equal("Available", VehiclesController.GetVehicleById(vehicle.Id)!.Status);
    }

    [Fact]
    public async Task Cancelling_an_already_cancelled_trip_is_rejected()
    {
        var (controller, owner, vehicle) = CreateController();
        var trip = await BookAsync(controller, owner.Id, vehicle.Id);

        await controller.Cancel(trip.Id, new CancelTripRequest());
        var second = await controller.Cancel(trip.Id, new CancelTripRequest());

        Assert.IsType<BadRequestObjectResult>(second.Result);
    }

    [Fact]
    public async Task Cancelling_an_already_completed_trip_is_rejected()
    {
        var (controller, owner, vehicle) = CreateController();
        var trip = await BookAsync(controller, owner.Id, vehicle.Id);

        await controller.End(trip.Id, new EndTripRequest(DateTime.UtcNow));
        var cancelResult = await controller.Cancel(trip.Id, new CancelTripRequest());

        Assert.IsType<BadRequestObjectResult>(cancelResult.Result);
    }

    [Fact]
    public async Task Another_users_trip_cannot_be_cancelled_by_a_non_admin()
    {
        var (owner, vehicle) = SeedFixture();
        var trip = await BookAsync(ControllerAs(owner.Id), owner.Id, vehicle.Id);

        var intruder = ControllerAs(owner.Id + 500000);
        var result = await intruder.Cancel(trip.Id, new CancelTripRequest());

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task An_admin_can_cancel_another_users_trip()
    {
        var (controller, owner, vehicle) = CreateController(callerIsOwner: false, callerIsAdmin: true);
        var trip = await BookAsync(controller, owner.Id, vehicle.Id);

        var result = await controller.Cancel(trip.Id, new CancelTripRequest());

        var cancelled = Assert.IsType<Trip>(Assert.IsType<ActionResult<Trip>>(result).Value);
        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task Cancelling_within_24h_of_the_scheduled_end_retains_a_partial_fee()
    {
        var (controller, owner, vehicle) = CreateController();
        var trip = await BookAsync(controller, owner.Id, vehicle.Id, duration: 4, durationType: "hour", totalFare: 40m);

        var result = await controller.Cancel(trip.Id, new CancelTripRequest());

        var cancelled = Assert.IsType<Trip>(Assert.IsType<ActionResult<Trip>>(result).Value);
        Assert.Equal(10m, cancelled.TotalFare); // 25% of 40 retained as fee; the other 30 was refunded
    }

    [Fact]
    public async Task Cancelling_a_nonexistent_trip_returns_not_found()
    {
        var (controller, _, _) = CreateController();

        var result = await controller.Cancel(999999, new CancelTripRequest());

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
