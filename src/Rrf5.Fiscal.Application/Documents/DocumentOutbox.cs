using Microsoft.Extensions.Logging;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>
/// Delivers outbox messages to <see cref="DocumentProcessor"/> (005 T020, ADR-005 items 2
/// and 3). Delivery is at least once: a message is claimed with <c>for update skip
/// locked</c> and stays locked while its document is processed. Only then is it marked
/// done or rescheduled in the same transaction. If the worker dies, the lock is released
/// and the message is delivered again. The processor is idempotent by the document state.
/// <para>
/// The message hop through a broker (ADR-005 item 2, RabbitMQ) is not in this step. Here
/// the worker reads the outbox directly, which keeps the same guarantees with one worker
/// process.
/// </para>
/// </summary>
public sealed partial class DocumentOutbox(IUnitOfWorkFactory database, DocumentProcessor processor, TimeProvider clock, ILogger<DocumentOutbox> logger)
{
    /// <summary>Processes the due messages of every active tenant, at most <paramref name="limit"/>; returns how many ran.</summary>
    public async Task<int> RunOnceAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var platform = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false))
        {
            tenants = await platform.Certificates.ListActiveTenantsAsync(cancellationToken).ConfigureAwait(false);
        }

        var processed = 0;
        foreach (var tenant in tenants)
        {
            while (processed < limit && await RunOneAsync(tenant, cancellationToken).ConfigureAwait(false))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>Runs the next due message of one tenant; false when there is none.</summary>
    public async Task<bool> RunOneAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var message = await uow.Documents.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return false;
        }

        ProcessResult result;
        try
        {
            result = await processor.ProcessAsync(tenantId, message.DocumentId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception, message.DocumentId, message.Attempts + 1);
            result = ProcessResult.Retry(TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(message.Attempts, 6)))));
        }

        if (result.Done)
        {
            await uow.Documents.CompleteAsync(message.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await uow.Documents.RescheduleAsync(message.Id, clock.GetUtcNow().Add(result.RetryAfter), cancellationToken).ConfigureAwait(false);
        }

        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Error, Message = "Processing document {resourceId} failed (attempt {count}); the message is rescheduled")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid resourceId, int count);
}
