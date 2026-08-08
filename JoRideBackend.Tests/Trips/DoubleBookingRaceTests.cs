using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JoRideBackend.Models;
using JoRideBackend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JoRideBackend.Tests.Trips;

/// <summary>
/// E3.1: fires many concurrent booking requests for the SAME vehicle/time slot through the
/// real ASP.NET Core pipeline (WebApplicationFactory — real threading, real per-vehicle
/// SemaphoreSlim in TripsController) and asserts exactly one wins.
///
/// Test users/vehicle are seeded directly via the controllers' public static Initialize
/// helpers rather than through HTTP registration — this is an in-process, single-AppDomain
/// shortcut for fixture setup only; the actual behavior under test (the booking race) still
/// goes through real HTTP requests handled by the real pipeline. One admin JWT is used to
/// authenticate all 20 requests (each specifying a different pre-seeded UserId in the body)
/// so the test doesn't need 20 separate register/login/KYC-approve round trips — admins are
/// exempt from the "caller must book for themselves" ownership check (see TripsController.Start).
///
/// The factory's real, DI-registered FirestoreService is swapped for an unconnected one
/// (empty config, matching the pattern used by the pure-unit trip tests). A booking created
/// here would otherwise persist to the actual dev Firestore project via
/// VehiclesController.SetStatus/TripsController.Start's Firestore writes — and since
/// TripsController.Initialize resets _nextId to 1 for an empty seed list, that new trip's ID
/// could collide with and overwrite a real trip document (this happened during earlier E3/E4
/// live-verification runs and had to be cleaned up by hand). Nothing about the concurrency
/// behavior under test depends on Firestore actually being connected.
/// </summary>
public class DoubleBookingRaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DoubleBookingRaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<FirestoreService>();
                services.AddSingleton(new FirestoreService(
                    new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FirestoreService>.Instance));
            });
        });
    }

    [Fact]
    public async Task Exactly_one_of_20_concurrent_bookings_for_the_same_vehicle_wins()
    {
        using var client = _factory.CreateClient();

        // Host (incl. FirestoreDataLoader's own Initialize call) is guaranteed started by now.
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopeFactory.CreateScope();
        var firestore = scope.ServiceProvider.GetRequiredService<FirestoreService>();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();

        const int concurrency = 20;
        const int vehicleId = 900001;

        var admin = new User
        {
            Id = 900000,
            Name = "Race Admin",
            Email = "race-admin@test.local",
            IsAdmin = true,
            IsActive = true,
            KycStatus = KycStatus.Approved,
            WalletBalance = 100000m,
        };

        var riders = Enumerable.Range(1, concurrency)
            .Select(i => new User
            {
                Id = 900000 + i,
                Name = $"Racer {i}",
                Email = $"racer{i}@test.local",
                IsActive = true,
                KycStatus = KycStatus.Approved,
                WalletBalance = 1000m,
            })
            .ToList();

        UsersController.Initialize(new List<User> { admin }.Concat(riders).ToList(), firestore);

        VehiclesController.Initialize(new List<Vehicle>
        {
            new()
            {
                Id = vehicleId,
                LicensePlate = "RACE-1",
                Category = "Economy",
                Status = "Available",
                IsVisible = true,
            }
        }, firestore);

        TripsController.Initialize(new List<Trip>(), firestore);

        var (token, _) = jwt.IssueToken(admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var tasks = riders.Select(rider =>
        {
            // "Credit Card" (simulated always-approved, see WalletController.TryChargeAsync)
            // keeps this test on the in-memory booking path only — no Postgres ledger
            // writes — so it isolates the thing actually under test (the per-vehicle
            // overlap lock) from the separate, already-covered ledger-writing code path.
            var body = new StartTripRequest(
                UserId: rider.Id,
                VehicleId: vehicleId,
                Duration: 1,
                DurationType: "hour",
                BaseFare: 8m,
                BookingFee: 0m,
                Tax: 0m,
                TotalFare: 8m,
                PaymentMethod: "Credit Card");
            return client.PostAsJsonAsync("/api/trips/start", body);
        }).ToArray();

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        // Two distinct, both-legitimate rejection paths can fire for a losing request:
        // - 409 Conflict from the definitive, lock-protected overlap re-check (HasOverlap) —
        //   the actual E3.1 race guard.
        // - 400 BadRequest from the pre-existing fast-path VehiclesController.IsAvailable
        //   check, which runs before the lock is acquired and will already see the winner's
        //   vehicle.Status == "InUse" if it loses the race to get there first.
        // Both mean the same thing to the caller: this vehicle is unavailable right now.
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.BadRequest).ToList();

        var other = responses.Where(r => r.StatusCode != HttpStatusCode.Created && r.StatusCode != HttpStatusCode.Conflict && r.StatusCode != HttpStatusCode.BadRequest).ToList();
        if (other.Count > 0)
        {
            var bodies = await Task.WhenAll(other.Select(r => r.Content.ReadAsStringAsync()));
            throw new Exception("Unexpected statuses: " + string.Join(" | ", other.Zip(bodies, (r, b) => $"{r.StatusCode}: {b}")));
        }

        Assert.Single(succeeded);
        Assert.Equal(concurrency - 1, rejected.Count);

        foreach (var r in rejected)
        {
            var text = await r.Content.ReadAsStringAsync();
            Assert.Contains("vehicle", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not available", StringComparison.OrdinalIgnoreCase),
                $"Expected a clear unavailability message, got: {text}");
        }

        // Exactly one InProgress trip for the vehicle must have been reserved — no
        // duplicate/corrupted entries from the race.
        var activeTripsForVehicle = TripsController.AllTrips().Count(t => t.VehicleId == vehicleId && t.Status == "InProgress");
        Assert.Equal(1, activeTripsForVehicle);
    }
}
