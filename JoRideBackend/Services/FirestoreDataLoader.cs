using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Identity;

namespace JoRideBackend.Services
{
    public class FirestoreDataLoader : IHostedService
    {
        private readonly FirestoreService _firestore;
        private readonly ILogger<FirestoreDataLoader> _logger;
        private readonly IPasswordHasher<User> _hasher;

        public FirestoreDataLoader(FirestoreService firestore, ILogger<FirestoreDataLoader> logger, IPasswordHasher<User> hasher)
        {
            _firestore = firestore;
            _logger    = logger;
            _hasher    = hasher;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            if (!_firestore.IsConnected)
            {
                _logger.LogInformation("Firestore not connected — all data starts in-memory.");
                await UsersController.EnsureSeedAdminAsync(_hasher);
                return;
            }

            _logger.LogInformation("Loading persisted data from Firestore...");

            var users = await _firestore.LoadUsersAsync();
            UsersController.Initialize(users, _firestore);
            await UsersController.EnsureSeedAdminAsync(_hasher);
            _logger.LogInformation("  Users loaded: {N}", users.Count);

            var trips = await _firestore.LoadTripsAsync();
            TripsController.Initialize(trips, _firestore);
            _logger.LogInformation("  Trips loaded: {N}", trips.Count);

            var notifs = await _firestore.LoadNotificationsAsync();
            NotificationsController.Initialize(notifs, _firestore);
            _logger.LogInformation("  Notifications loaded: {N}", notifs.Count);

            var txns = await _firestore.LoadTransactionsAsync();
            WalletController.Initialize(txns, _firestore);
            _logger.LogInformation("  Wallet transactions loaded: {N}", txns.Count);

            var auditLogs = await _firestore.LoadAuditLogsAsync();
            AuditController.Initialize(auditLogs, _firestore);
            _logger.LogInformation("  Audit logs loaded: {N}", auditLogs.Count);

            var vehicles = await _firestore.LoadVehiclesAsync();
            if (vehicles.Count > 0)
            {
                VehiclesController.Initialize(vehicles, _firestore);
                _logger.LogInformation("  Vehicles loaded: {N}", vehicles.Count);
            }
            else
            {
                // First run — seed defaults and persist them to Firestore
                VehiclesController.Seed();
                VehiclesController.SetFirestore(_firestore);
                var seeded = VehiclesController.AllVehicles();
                foreach (var v in seeded) await _firestore.SaveVehicleAsync(v);
                _logger.LogInformation("  Vehicles seeded and saved to Firestore: {N}", seeded.Count);
            }

            var pricings = await _firestore.LoadPricingsAsync();
            if (pricings.Count > 0)
            {
                PricingController.Initialize(pricings, _firestore);
                _logger.LogInformation("  Pricings loaded: {N}", pricings.Count);
            }
            else
            {
                PricingController.Seed();
                PricingController.SetFirestore(_firestore);
                var seeded = PricingController.AllPricings();
                foreach (var p in seeded) await _firestore.SavePricingAsync(p);
                _logger.LogInformation("  Pricings seeded and saved to Firestore: {N}", seeded.Count);
            }
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
