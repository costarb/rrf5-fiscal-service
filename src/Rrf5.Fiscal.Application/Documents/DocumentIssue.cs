using System.Globalization;
using System.Text.Json;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>Pre-validation failed while issuing: nothing was stored and no number was reserved.</summary>
public sealed class DocumentInvalidException(PrevalidationReport report) : Exception("The document did not pass pre-validation.")
{
    public PrevalidationReport Report { get; } = report;
}

/// <summary>
/// 005 T020, T021 and T024 (ADR-005 items 1, 4 and 5): one entry point for both emission
/// modes. In one transaction it:
/// <list type="bullet">
/// <item>claims the idempotency key;</item>
/// <item>runs the pre-validation (E2);</item>
/// <item>stores the document in <c>READY_TO_SIGN</c> with its items, payments and the snapshot
/// of the approved rule versions;</item>
/// <item>writes a <c>PROCESS</c> message to the outbox.</item>
/// </list>
/// No SEFAZ call and no A1 here; the number is reserved later by the worker, right before
/// signing (FE-008). The same key and payload return the same document. Another payload
/// is a conflict, and neither case reserves a number.
/// </summary>
public sealed class DocumentIssue(IUnitOfWorkFactory database, DocumentPrevalidation prevalidation, TimeProvider clock)
{
    public const string Operation = "document.issue";

    public async Task<IdempotentResponse> IssueAsync(
        Caller caller, Guid environmentId, MutationRequest request, DocumentInput input, string emissionMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.DocumentsIssue);
        if (emissionMode is not ("SYNC" or "ASYNC"))
        {
            throw new ArgumentException("emissionMode must be SYNC or ASYNC.", nameof(emissionMode));
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var claim = new IdempotencyClaim(caller.TenantId, null, Operation, request.Key, request.RequestHash);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, claim, async ct =>
        {
            var now = clock.GetUtcNow();
            var report = await prevalidation.CheckAsync(uow, caller, environmentId, input, now, ct).ConfigureAwait(false);
            if (!report.Valid)
            {
                throw new DocumentInvalidException(report);
            }

            var environment = (await uow.Issuer.GetEnvironmentAsync(environmentId, ct).ConfigureAwait(false))!;
            var id = Guid.NewGuid();
            var items = report.Items.Select(i => new DocumentItemRecord(
                i.Number, i.Input.ProductCode!, i.Input.Description!, i.Rule!.Rule.ItemClass, i.Input.Unit!, i.Quantity, i.UnitPrice,
                i.LineValue, i.Discount, i.Surcharge, i.Rule.Version.Id, i.Input.Gtin!, i.Input.Ncm!, i.Input.Cest, i.Input.Origin!,
                i.Input.Cfop!, i.Input.Csosn!)).ToList();
            var payments = report.Payments.Select(p => new DocumentPaymentRecord(
                p.Number, p.Input.Method!, p.TPag!, p.Input.Description, p.Amount, p.Input.Card?.Integration, p.Input.Card?.AcquirerCnpj,
                p.Input.Card?.Brand, p.Input.Card?.AuthorizationCode)).ToList();
            var snapshot = JsonSerializer.SerializeToElement(new
            {
                nature = input.Nature,
                operation = input.Operation,
                seriesId = report.Series!.Id,
                rules = report.Items.Select(i => i.Rule!).DistinctBy(r => r.Version.Id).Select(r => new
                {
                    versionId = r.Version.Id,
                    ruleId = r.Rule.Id,
                    itemClass = r.Rule.ItemClass,
                    payload = r.Version.Payload,
                }),
            });

            await uow.Documents.InsertAsync(
                new NewDocument(id, caller.TenantId, environment.EstablishmentId, environment.Id, report.Series.Id, input.ExternalId!, request.RequestHash,
                    emissionMode, report.Totals!.Total, report.Totals.Change, input.Consumer?.Cpf, input.Consumer?.Name, snapshot),
                items, payments, ct).ConfigureAwait(false);
            await uow.Documents.EnqueueAsync(id, "PROCESS", now, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(new AuditEntry(caller.TenantId, caller.Principal, "document.issue", "fiscal_document", id.ToString("D"),
                AuditOutcome.Succeeded, caller.CorrelationId, new Dictionary<string, string?>
                {
                    ["environmentId"] = environment.Id.ToString("D"),
                    ["mode"] = emissionMode,
                    ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(202, "fiscal_document", id, null));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>The document as the contract shows it (<c>FiscalDocument</c>).</summary>
public sealed record DocumentView(
    Guid Id, string ExternalId, string State, bool Terminal, string? EmissionMode, int? Series, long? Number, string? AccessKey,
    string? Total, ProtocolRecord? Answer);

/// <summary>Reads documents for the caller's establishments (scope <c>documents.read</c> or <c>documents.issue</c>).</summary>
public sealed class DocumentQueries(IUnitOfWorkFactory database)
{
    public async Task<DocumentView> GetAsync(Caller caller, Guid documentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.DocumentsRead) && !caller.Scopes.Contains(Scopes.DocumentsIssue))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var document = await uow.Documents.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is null
            || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(document.EstablishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        int? series = document.SeriesId is { } seriesId
            ? (await uow.Issuer.GetSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false))?.Series
            : null;
        var answer = document.State is DocumentStates.Authorized or DocumentStates.Rejected
            ? await uow.Documents.LatestProtocolAsync(document.Id, cancellationToken).ConfigureAwait(false)
            : null;
        return new DocumentView(
            document.Id, document.ExternalId, document.State, DocumentStates.IsTerminal(document.State), document.EmissionMode, series,
            document.Number, document.AccessKey, document.Total?.ToString("0.00", CultureInfo.InvariantCulture), answer);
    }
}
