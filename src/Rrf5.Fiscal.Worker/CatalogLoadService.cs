using Rrf5.Fiscal.Application.Regulatory;

namespace Rrf5.Fiscal.Worker;

/// <summary>
/// Loads the shipped catalog into <c>schema_version</c> at start (T033). Refused
/// entries are logged with their reason; a failure is retried on the next start.
/// </summary>
public sealed partial class CatalogLoadService(CatalogLoader loader, ILogger<CatalogLoadService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var report = await loader.LoadAsync(stoppingToken).ConfigureAwait(false);
            LogLoaded(logger, report.Inserted, report.Updated, report.Unchanged, report.Refused.Count);
            foreach (var refused in report.Refused)
            {
                LogRefused(logger, refused.CatalogReferenceId, refused.Reason);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 3200, Level = LogLevel.Information, Message = "Catalog loaded: {count} inserted, {version} updated, {status} unchanged, {code} refused")]
    private static partial void LogLoaded(ILogger logger, int count, int version, int status, int code);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "Catalog entry {resourceId} refused: {reason}")]
    private static partial void LogRefused(ILogger logger, string resourceId, string reason);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Error, Message = "Catalog load failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
