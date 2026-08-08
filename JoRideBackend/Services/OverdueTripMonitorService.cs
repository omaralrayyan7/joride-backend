using JoRideBackend.Models;

namespace JoRideBackend.Services
{
    /// <summary>
    /// E4.3: periodically flags InProgress trips still open more than a grace period past
    /// their ScheduledEndTime and sends the renter a one-time notification. This is detection
    /// and notification ONLY — it never touches DeviceCommandService or a vehicle's lock
    /// state. Per the delivery plan, escalating an overdue trip to an immobilize command is a
    /// deliberate admin action taken through the existing E1.3 DeviceCommandsController, not
    /// something this service (or any background process) should ever do automatically.
    /// </summary>
    public class OverdueTripMonitorService : BackgroundService
    {
        public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OverdueTripMonitorService> _logger;

        public OverdueTripMonitorService(IServiceScopeFactory scopeFactory, ILogger<OverdueTripMonitorService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                using var scope = _scopeFactory.CreateScope();
                var sms = scope.ServiceProvider.GetRequiredService<ISmsService>();
                await FlagAndNotifyAsync(TripsController.AllTrips(), TripsController._firestore, sms, _logger, DateTime.UtcNow);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>Pure-ish core: given the full trip set, flags+notifies whichever ones just
        /// crossed the overdue threshold and returns them. Public and static so it's directly
        /// unit-testable without a running BackgroundService/DI container.</summary>
        public static IEnumerable<Trip> FindNewlyOverdue(IEnumerable<Trip> allTrips, DateTime nowUtc) =>
            allTrips.Where(t => t.Status == "InProgress"
                             && t.EndTime is null
                             && t.OverdueFlaggedAt is null
                             && t.ScheduledEndTime.HasValue
                             && nowUtc > t.ScheduledEndTime.Value.ToUniversalTime() + GracePeriod);

        public static async Task<List<Trip>> FlagAndNotifyAsync(
            IEnumerable<Trip> allTrips, FirestoreService? firestore, ISmsService sms, ILogger logger, DateTime nowUtc)
        {
            var newlyOverdue = FindNewlyOverdue(allTrips, nowUtc).ToList();

            foreach (var trip in newlyOverdue)
            {
                trip.OverdueFlaggedAt = nowUtc;
                await (firestore?.SaveTripAsync(trip) ?? Task.CompletedTask);

                var user = UsersController.GetUser(trip.UserId);
                var vehicle = VehiclesController.GetVehicleById(trip.VehicleId);
                var minutesOverdue = (int)Math.Round((nowUtc - trip.ScheduledEndTime!.Value.ToUniversalTime()).TotalMinutes);

                NotificationsController.Push(
                    trip.UserId,
                    "Trip Overdue",
                    $"Your trip on vehicle #{trip.VehicleId} ({vehicle?.LicensePlate}) is {minutesOverdue} minutes overdue. Please return the vehicle or contact support.",
                    "overdue");

                if (!string.IsNullOrWhiteSpace(user?.Phone))
                {
                    try
                    {
                        await sms.SendSmsAsync(user.Phone,
                            $"JoRide: your trip #{trip.Id} is overdue by {minutesOverdue} min. Please return the vehicle soon.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[OverdueMonitor] Failed to send SMS for trip {TripId}", trip.Id);
                    }
                }

                AuditController.Log("TripOverdue", "Trip", trip.Id,
                    $"User: {user?.Name ?? "?"} (#{trip.UserId})", "System",
                    $"Vehicle #{trip.VehicleId} ({vehicle?.LicensePlate}) overdue by {minutesOverdue} minute(s). Notification sent, no auto-immobilize.");

                logger.LogInformation("[OverdueMonitor] Trip {TripId} flagged overdue by {Minutes} minute(s).", trip.Id, minutesOverdue);
            }

            return newlyOverdue;
        }
    }
}
