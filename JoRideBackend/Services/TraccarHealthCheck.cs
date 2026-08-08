using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JoRideBackend.Services
{
    public class TraccarHealthCheck : IHealthCheck
    {
        private readonly TraccarService _traccar;

        public TraccarHealthCheck(TraccarService traccar)
        {
            _traccar = traccar;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var reachable = await _traccar.CheckServerHealthAsync(cancellationToken);
            return reachable
                ? HealthCheckResult.Healthy("Traccar server reachable.")
                : HealthCheckResult.Unhealthy("Traccar server unreachable or not configured.");
        }
    }
}
