using Xunit;

// TripsController/UsersController/VehiclesController/WalletController hold process-static,
// in-memory state (see CLAUDE.md's "State is in-memory and process-local"). Several test
// classes (PayoutReportTests, DoubleBookingRaceTests, CancellationEndpointTests) seed that
// shared static state directly via each controller's Initialize helper. xUnit runs different
// test classes in parallel by default, which would let those seeds race and corrupt each
// other's fixtures. Disabling parallelization keeps this small, fast suite deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
