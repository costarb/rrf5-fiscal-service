using Rrf5.Fiscal.Application.Certificates;

namespace Rrf5.Fiscal.Worker;

/// <summary>
/// Runs <see cref="CertificateExpiryAlerts"/> at start and then hourly (T025). The job
/// itself guarantees one alert per certificate and threshold per Sao Paulo day, so the
/// hourly cadence only makes the daily alert robust to restarts and missed runs.
/// A failed run is logged and retried on the next tick; it never stops the worker.
/// </summary>
public sealed partial class CertificateExpiryAlertService(CertificateExpiryAlerts alerts, ILogger<CertificateExpiryAlertService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var count = await alerts.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                LogRun(logger, count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Certificate expiry check emitted {count} alerts")]
    private static partial void LogRun(ILogger logger, int count);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "Certificate expiry check failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
