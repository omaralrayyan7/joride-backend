using System.Security.Claims;
using JoRideBackend.Data;
using JoRideBackend.Models;
using JoRideBackend.Models.Payments;
using JoRideBackend.Services;
using JoRideBackend.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JoRideBackend.Tests.Payments;

public class HyperPayCheckoutControllerTests
{
    private static int _seed = 500000;

    private static (HyperPayCheckoutController Controller, PaymentsDbContext Db, User User) CreateController(bool callerIsOwner = true, bool callerIsAdmin = false)
    {
        var id = System.Threading.Interlocked.Increment(ref _seed);
        var firestore = new FirestoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance);

        var user = new User { Id = id, Name = $"User {id}", Email = $"u{id}@test.local", IsActive = true };
        UsersController.Initialize(new List<User> { user }, firestore);

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new PaymentsDbContext(options);

        var controller = new HyperPayCheckoutController(db, new FakePaymentGateway());
        var callerId = callerIsOwner ? user.Id : user.Id + 900000;
        var claims = new List<Claim> { new("sub", callerId.ToString()) };
        if (callerIsAdmin) claims.Add(new Claim("role", "admin"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };

        return (controller, db, user);
    }

    [Fact]
    public async Task Creates_a_PaymentIntent_and_returns_the_checkout_id()
    {
        var (controller, db, user) = CreateController();

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 25.50m), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var paymentIntentId = (Guid)body.GetType().GetProperty("paymentIntentId")!.GetValue(body)!;
        var checkoutId = (string)body.GetType().GetProperty("checkoutId")!.GetValue(body)!;
        var state = (string)body.GetType().GetProperty("state")!.GetValue(body)!;

        Assert.False(string.IsNullOrEmpty(checkoutId));
        Assert.Equal("Created", state);

        var stored = await db.PaymentIntents.SingleAsync(p => p.Id == paymentIntentId);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(25.50m, stored.Amount);
        Assert.Equal(PaymentIntentState.Created, stored.State);
        Assert.Null(stored.TripId);
    }

    [Fact]
    public async Task Links_the_PaymentIntent_to_a_trip_when_a_valid_TripId_is_given()
    {
        var (controller, db, user) = CreateController();
        var firestore = new FirestoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance);
        var trip = new Trip { Id = 1, UserId = user.Id, VehicleId = 1, Status = "InProgress" };
        TripsController.Initialize(new List<Trip> { trip }, firestore);

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 10m, trip.Id), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var paymentIntentId = (Guid)ok.Value!.GetType().GetProperty("paymentIntentId")!.GetValue(ok.Value)!;
        var stored = await db.PaymentIntents.SingleAsync(p => p.Id == paymentIntentId);
        Assert.Equal(trip.Id, stored.TripId);
    }

    [Fact]
    public async Task Rejects_a_TripId_that_belongs_to_a_different_user()
    {
        var (controller, _, user) = CreateController();
        var firestore = new FirestoreService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance);
        var trip = new Trip { Id = 2, UserId = user.Id + 12345, VehicleId = 1, Status = "InProgress" };
        TripsController.Initialize(new List<Trip> { trip }, firestore);

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 10m, trip.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Rejects_a_non_positive_amount()
    {
        var (controller, _, user) = CreateController();

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 0m), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_non_admin_cannot_start_a_checkout_for_another_user()
    {
        var (controller, _, user) = CreateController(callerIsOwner: false);

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 10m), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task An_admin_can_start_a_checkout_for_another_user()
    {
        var (controller, db, user) = CreateController(callerIsOwner: false, callerIsAdmin: true);

        var result = await controller.CreateCheckout(new CreateHyperPayCheckoutRequest(user.Id, 10m), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var paymentIntentId = (Guid)ok.Value!.GetType().GetProperty("paymentIntentId")!.GetValue(ok.Value)!;
        Assert.True(await db.PaymentIntents.AnyAsync(p => p.Id == paymentIntentId && p.UserId == user.Id));
    }
}
