namespace JoRideBackend.Services
{
    /// <summary>
    /// Periodically polls Traccar's REST API for current device positions and logs
    /// them. Log-only for now — telemetry_snapshot persistence lands in E1.4.
    /// </summary>
    public class TraccarPollingService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly TraccarService _traccar;
        private readonly ILogger<TraccarPollingService> _logger;

        public TraccarPollingService(TraccarService traccar, ILogger<TraccarPollingService> logger)
        {
            _traccar = traccar;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_traccar.IsRestConfigured)
            {
                _logger.LogWarning(
                    "[TraccarPolling] TRACCAR_BASE_URL/TRACCAR_TOKEN not configured — position polling disabled.");
                return;
            }

            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                await PollOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task PollOnceAsync(CancellationToken ct)
        {
            try
            {
                var positions = await _traccar.GetAllPositionsAsync(ct);
                foreach (var position in positions)
                {
                    _logger.LogInformation(
                        "[TraccarPolling] deviceId={DeviceId} deviceTime={DeviceTime:o} lat={Lat} lon={Lon} speed={Speed}",
                        position.DeviceId, position.DeviceTime, position.Latitude, position.Longitude, position.Speed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutting down — not an error
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TraccarPolling] Failed to poll Traccar positions.");
            }
        }
    }
}
