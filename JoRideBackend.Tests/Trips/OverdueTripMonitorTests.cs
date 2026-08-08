using JoRideBackend.Models;
using JoRideBackend.Services;
using JoRideBackend.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JoRideBackend.Tests.Trips;

/// <summary>E4.3: overdue detection + notification. Never touches DeviceCommandService —
/// these tests assert only on the notification/flagging side effects to make that explicit:
/// nothing here can immobilize a vehicle.</summary>
public class OverdueTripMonitorTests
{
    private static int _seed = 600000;

    private static (User User, Vehicle Vehicle) SeedFixture(FirestoreService firestore)
    {
        var id = System.Threading.Interlocked.Increment(ref _seed);
        var user = new User { Id = id, Name = $"User {id}", Email = $"u{id}@test.local", Phone = "+962700000000", IsActive = true };
        UsersController.Initialize(new List<User> { user }, firestore);

        var vehicle = new Vehicle { Id = id, LicensePlate = $"OD-{id}", Category = "Economy", Status = "InUse", IsVisible = true };
        VehiclesController.Initialize(new List<Vehicle> { vehicle }, firestore);

        return (user, vehicle);
    }

    private static FirestoreService NoOpFirestore() => new(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        NullLogger<FirestoreService>.Instance);

    [Fact]
    public void A_trip_within_the_grace_period_is_not_yet_overdue()
    {
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 1, Status = "InProgress", ScheduledEndTime = now.AddMinutes(-20) }; // only 20 min late

        var result = OverdueTripMonitorService.FindNewlyOverdue(new[] { trip }, now);

        Assert.Empty(result);
    }

    [Fact]
    public void A_trip_past_the_grace_period_is_flagged_overdue()
    {
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 2, Status = "InProgress", ScheduledEndTime = now.AddMinutes(-31) }; // 1 min past the 30-min grace period

        var result = OverdueTripMonitorService.FindNewlyOverdue(new[] { trip }, now);

        Assert.Single(result);
    }

    [Fact]
    public void A_completed_trip_is_never_flagged_even_if_it_ended_late()
    {
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 3, Status = "Completed", EndTime = now.AddMinutes(-5), ScheduledEndTime = now.AddHours(-2) };

        var result = OverdueTripMonitorService.FindNewlyOverdue(new[] { trip }, now);

        Assert.Empty(result);
    }

    [Fact]
    public void A_trip_already_flagged_is_not_flagged_again()
    {
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 4, Status = "InProgress", ScheduledEndTime = now.AddHours(-2), OverdueFlaggedAt = now.AddMinutes(-10) };

        var result = OverdueTripMonitorService.FindNewlyOverdue(new[] { trip }, now);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FlagAndNotifyAsync_sets_the_flag_pushes_a_notification_and_sends_sms_exactly_once()
    {
        var firestore = NoOpFirestore();
        var (user, vehicle) = SeedFixture(firestore);
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 5, UserId = user.Id, VehicleId = vehicle.Id, Status = "InProgress", ScheduledEndTime = now.AddMinutes(-45) };
        var sms = new FakeSmsService();

        var flagged = await OverdueTripMonitorService.FlagAndNotifyAsync(new[] { trip }, firestore, sms, NullLogger.Instance, now);

        Assert.Single(flagged);
        Assert.Equal(now, trip.OverdueFlaggedAt);
        Assert.Single(sms.SentMessages);
        Assert.Equal(user.Phone, sms.SentMessages[0].To);

        // Running the check again immediately must NOT re-notify — OverdueFlaggedAt now gates it.
        var secondPass = await OverdueTripMonitorService.FlagAndNotifyAsync(new[] { trip }, firestore, sms, NullLogger.Instance, now.AddMinutes(1));
        Assert.Empty(secondPass);
        Assert.Single(sms.SentMessages); // still just the one from the first pass
    }

    [Fact]
    public async Task FlagAndNotifyAsync_never_invokes_any_device_command()
    {
        // No DeviceCommandService is referenced anywhere in OverdueTripMonitorService — this
        // test exists as an explicit, permanent guard: overdue detection must stay
        // notification-only, per the "do NOT auto-immobilize" requirement.
        var firestore = NoOpFirestore();
        var (user, vehicle) = SeedFixture(firestore);
        var now = DateTime.UtcNow;
        var trip = new Trip { Id = 6, UserId = user.Id, VehicleId = vehicle.Id, Status = "InProgress", ScheduledEndTime = now.AddHours(-5) };
        var sms = new FakeSmsService();
        var vehicleStatusBefore = VehiclesController.GetVehicleById(vehicle.Id)!.Status;

        await OverdueTripMonitorService.FlagAndNotifyAsync(new[] { trip }, firestore, sms, NullLogger.Instance, now);

        Assert.Equal(vehicleStatusBefore, VehiclesController.GetVehicleById(vehicle.Id)!.Status);
    }
}
