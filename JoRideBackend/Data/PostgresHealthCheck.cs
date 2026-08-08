using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JoRideBackend.Data
{
    public class PostgresHealthCheck : IHealthCheck
    {
        private readonly PaymentsDbContext _dbContext;

        public PostgresHealthCheck(PaymentsDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
                return canConnect
                    ? HealthCheckResult.Healthy("Postgres connection OK.")
                    : HealthCheckResult.Unhealthy("Cannot connect to Postgres.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Postgres health check threw an exception.", ex);
            }
        }
    }
}
