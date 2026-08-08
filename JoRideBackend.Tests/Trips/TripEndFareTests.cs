using System.Security.Claims;
using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JoRideBackend.Tests.Trips;

/// <summary>
/// E4.1/E4.2: confirms TripsController.End() correctly rolls PricingController's graduated
/// overtime fare into the trip's TotalFare and charges it through the existing
/// WalletController payment path (the only payment path trips have ever used — see
/// TripsController.cs's Start/End; PaymentIntent/Capture is a separate, unrelated
/// card-top-up subsystem this codebase has never wired trips through).
/// </summary>
public class TripEndFareTests
{
    private static int _seed = 700000;

    private static (TripsController Controller, User User, Vehicle Vehicle) Seed()
    {
        var id = System.Threading.Interlocked.Increment(ref _seed);
        WalletController.SetServiceScopeFactory(null!);
        var firestore = new FirestoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance);

        var user = new User { Id = id, Name = $"User {id}", Email = $"u{id}@test.local", IsActive = true, KycStatus = KycStatus.Approved, WalletBalance = 1000m };
        UsersController.Initialize(new List<User> { user }, firestore);

        var vehicle = new Vehicle { Id = id, LicensePlate = $"T-{id}", Category = "Economy", Status = "Available", IsVisible = true };
        VehiclesController.Initialize(new List<Vehicle> { vehicle }, firestore);

        PricingController.Initialize(new List<JoRideBackend.Models.Pricing>
        {
            new() { Category = "Economy", MinuteRate = 0.15m, HourlyRate = 8m, DailyRate = 45m, IsActive = true },
        }, firestore);

        TripsController.Initialize(new List<Trip>(), firestore);

        var controller = new TripsController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", id.ToString()) }, "Test")),
                },
            },
        };

        return (controller, user, vehicle);
    }

    private static async Task<Trip> BookAsync(TripsController controller, int userId, int vehicleId)
    {
        var request = new StartTripRequest(userId, vehicleId, 1, "min", 0.15m, 0m, 0m, 0.15m, "joRide Wallet");
        var result = await controller.Start(request);
        return Assert.IsType<Trip>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
    }

    [Theory]
    [InlineData(90, 12.5)]      // 1h30m over -> 1*8 + 30*0.15
    [InlineData(3, 0.45)]       // very short overtime
    [InlineData(1450, 46.5)]    // crosses hourly -> daily tier: 1*45 + 10*0.15
    [InlineData(60, 8.0)]       // exact hour
    public async Task End_bills_graduated_overtime_and_charges_it_via_the_wallet_path(int overtimeMinutes, decimal expectedOvertimeFare)
    {
        var (controller, user, vehicle) = Seed();
        var trip = await BookAsync(controller, user.Id, vehicle.Id);
        var startingBalance = user.WalletBalance;

        var endTime = trip.ScheduledEndTime!.Value.AddMinutes(overtimeMinutes);
        var result = await controller.End(trip.Id, new EndTripRequest(endTime));

        var ended = Assert.IsType<Trip>(Assert.IsAssignableFrom<ActionResult<Trip>>(result).Value);
        Assert.Equal(expectedOvertimeFare, ended.OvertimeFare);
        Assert.Equal("Paid", ended.OvertimePaymentStatus);
        Assert.Equal(0.15m + expectedOvertimeFare, ended.TotalFare); // base fare + overtime rolled in
        Assert.Equal(startingBalance - expectedOvertimeFare, user.WalletBalance); // charged via WalletController, not a separate path
    }

    [Fact]
    public async Task On_time_end_charges_no_overtime()
    {
        var (controller, user, vehicle) = Seed();
        var trip = await BookAsync(controller, user.Id, vehicle.Id);

        var result = await controller.End(trip.Id, new EndTripRequest(trip.ScheduledEndTime!.Value));

        var ended = Assert.IsType<Trip>(Assert.IsAssignableFrom<ActionResult<Trip>>(result).Value);
        Assert.Equal(0m, ended.OvertimeFare);
        Assert.Equal("None", ended.OvertimePaymentStatus);
    }
}
