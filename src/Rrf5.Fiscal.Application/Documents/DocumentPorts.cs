using System.Text.Json;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>States of <c>FiscalDocument.status</c> (closed enum, data-model.md, migration 0011).</summary>
public static class DocumentStates
{
    public const string ReadyToSign = "READY_TO_SIGN";
    public const string Signing = "SIGNING";
    public const string SignedState = "SIGNED";
    public const string Queued = "QUEUED";
    public const string Sending = "SENDING";
    public const string WaitingSefaz = "WAITING_SEFAZ";
    public const string Authorized = "AUTHORIZED";
    public const string Rejected = "REJECTED";
    public const string Error = "ERROR";

    public static bool IsTerminal(string state) => state is Authorized or Rejected or "CANCELLED";
}

public sealed record NewDocument(
    Guid Id, Guid TenantId, Guid EstablishmentId, Guid EnvironmentId, Guid SeriesId, string ExternalId, string RequestHash,
    string EmissionMode, decimal Total, decimal Change, string? ConsumerCpf, string? ConsumerName, JsonElement TaxRuleSnapshot);

public sealed record DocumentItemRecord(
    int Number, string ProductCode, string Description, string ItemClass, string Unit, decimal Quantity, decimal UnitPrice,
    decimal LineValue, decimal Discount, decimal Surcharge, Guid TaxRuleVersionId, string Gtin, string Ncm, string? Cest,
    string Origin, string Cfop, string Csosn);

public sealed record DocumentPaymentRecord(
    int Number, string Method, string TPag, string? Description, decimal Amount, int? CardIntegration, string? AcquirerCnpj,
    string? CardBrand, string? AuthorizationCode);

public sealed record DocumentRecord(
    Guid Id, Guid TenantId, Guid EstablishmentId, Guid EnvironmentId, Guid? SeriesId, string ExternalId, string State,
    string? EmissionMode, long? Number, string? AccessKey, DateTimeOffset? IssuedAt, DateTimeOffset? SignedAt, string? DigVal,
    decimal? Total, decimal Change, string? ConsumerCpf, string? ConsumerName, JsonElement? TaxRuleSnapshot,
    string? SignedXmlSha256, string? SentXmlObjectKey, string? SentXmlObjectVersion,
    string? ProcXmlSha256, string? ProcXmlObjectKey, string? ProcXmlObjectVersion, DateTimeOffset? AuthorizedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Fields frozen at the signed boundary (migration 0011 guard).</summary>
public sealed record SignedFields(
    DateTimeOffset SignedAt, string DigVal, string SignedXmlSha256, string CertificateFingerprint, string SchemaReferenceId,
    string SentXmlObjectKey, string? SentXmlObjectVersion);

public sealed record NewExchange(
    Guid Id, Guid DocumentId, string Operation, string Outcome, int? CStat, string RequestSha256, string? RequestObjectKey,
    string? RequestObjectVersion, string? ResponseSha256, string? ResponseObjectKey, string? ResponseObjectVersion,
    int? HttpStatus, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

public sealed record NewProtocol(
    Guid ExchangeId, Guid DocumentId, string Kind, int CStat, string XMotivo, string? NProt, string? ChNfe, string? DigVal, DateTimeOffset ReceivedAt);

public sealed record ProtocolRecord(string Kind, int CStat, string XMotivo, string? NProt, DateTimeOffset ReceivedAt);

public sealed record OutboxMessage(Guid Id, Guid DocumentId, string Kind, int Attempts);

/// <summary>Documents, their emission records and the outbox, under the unit of work's tenant (005 Phase 6).</summary>
public interface IDocumentRepository
{
    Task InsertAsync(NewDocument document, IReadOnlyList<DocumentItemRecord> items, IReadOnlyList<DocumentPaymentRecord> payments, CancellationToken cancellationToken);

    Task<DocumentRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Locks the document row for this transaction (<c>for update</c>); null when it does not exist.</summary>
    Task<DocumentRecord?> LockAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentItemRecord>> ListItemsAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentPaymentRecord>> ListPaymentsAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Binds a reserved number to the document (RESERVED to BOUND) and sets series, number, key and issue time.</summary>
    Task BindNumberAsync(Guid documentId, Guid reservationId, Guid seriesId, long number, string accessKey, DateTimeOffset issuedAt, CancellationToken cancellationToken);

    /// <summary>Refreshes the issue time of a document that is not signed yet (the key does not change: it carries only AAMM).</summary>
    Task RefreshIssuedAtAsync(Guid documentId, DateTimeOffset issuedAt, CancellationToken cancellationToken);

    Task MarkSignedAsync(Guid documentId, SignedFields fields, CancellationToken cancellationToken);

    Task SetStateAsync(Guid documentId, string state, CancellationToken cancellationToken);

    Task MarkAuthorizedAsync(Guid documentId, string procSha256, string procObjectKey, string? procObjectVersion, DateTimeOffset authorizedAt, CancellationToken cancellationToken);

    Task InsertExchangeAsync(NewExchange exchange, CancellationToken cancellationToken);

    Task InsertProtocolAsync(NewProtocol protocol, CancellationToken cancellationToken);

    Task<int> CountExchangesAsync(Guid documentId, string operation, CancellationToken cancellationToken);

    Task<ProtocolRecord?> LatestProtocolAsync(Guid documentId, CancellationToken cancellationToken);

    Task EnqueueAsync(Guid documentId, string kind, DateTimeOffset availableAt, CancellationToken cancellationToken);

    /// <summary>The oldest due message, locked with <c>skip locked</c> so concurrent workers never take the same one.</summary>
    Task<OutboxMessage?> ClaimNextAsync(CancellationToken cancellationToken);

    Task CompleteAsync(Guid messageId, CancellationToken cancellationToken);

    Task RescheduleAsync(Guid messageId, DateTimeOffset availableAt, CancellationToken cancellationToken);
}
