using Rrf5.Fiscal.Application.Documents;

namespace Rrf5.Fiscal.Worker;

/// <summary>
/// Drains the document outbox (005 T020): runs due messages, then waits a second when
/// there was nothing to do. A failed round is logged and the loop goes on.
/// </summary>
public sealed partial class DocumentOutboxService(DocumentOutbox outbox, ILogger<DocumentOutboxService> logger) : BackgroundService
{
    private const int BatchLimit = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await outbox.RunOnceAsync(BatchLimit, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            if (processed == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 5011, Level = LogLevel.Error, Message = "Document outbox round failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
