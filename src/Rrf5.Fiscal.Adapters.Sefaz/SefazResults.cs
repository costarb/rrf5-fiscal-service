namespace Rrf5.Fiscal.Adapters.Sefaz;

/// <summary>
/// Normalized result of one exchange with the authorizer (ADR-007 item 4). Only
/// <see cref="Authorized"/> may lead to the AUTHORIZED state; everything after the
/// request may have left the process and is not a definite answer leads to a
/// consultation by access key, never to a resend (ADR-005 item 6, FE-010).
/// </summary>
public enum SefazOutcome
{
    /// <summary>The request never left the process (connection or TLS failure): safe to try again.</summary>
    NotSent,

    /// <summary>The request may have reached the authorizer and no definite answer came back: consult by key.</summary>
    Uncertain,

    /// <summary>Protocol cStat 100 with the same chNFe and digVal as the document sent.</summary>
    Authorized,

    /// <summary>Protocol with a denial code: 110 (Anexo I §4.4.1) or 301, 302, 303 (§4.4.3). Final; the number is used.</summary>
    Denied,

    /// <summary>The batch or the document was refused with an official code; no protocol of use exists.</summary>
    Rejected,

    /// <summary>Batch received for asynchronous processing (cStat 103, Visão Geral §5.1.4.2): consult by key.</summary>
    Received,

    /// <summary>The service is paused (cStat 108 or 109, Anexo I §4.4.1); the batch was not processed.</summary>
    ServiceUnavailable,

    /// <summary>Consultation: cStat 217, the document is not in the authorizer's base.</summary>
    NotFound,

    /// <summary>A protocol of use for this key carries a different digVal: another document holds the key. Manual review.</summary>
    Mismatch,

    /// <summary>A well-formed answer this version does not act on (for example 150, 101, 151 or tpAmb other than expected). Manual review.</summary>
    Unexpected,
}

/// <summary>The <c>protNFe/infProt</c> fields, with the element kept byte for byte for <c>nfeProc</c>.</summary>
public sealed record SefazProtocol(
    string AccessKey, string? ProtocolNumber, string? ReceivedAt, string? DigestValue, string Status, string Reason, string Xml);

/// <summary>
/// The trace of one exchange (ADR-007 item 6): what was sent and received, for immutable
/// storage by the caller. The bodies are never logged.
/// </summary>
public sealed record SefazExchange(
    string Service, Uri Endpoint, DateTimeOffset StartedAt, long ElapsedMilliseconds, string Request, int? HttpStatus, string? Response);

/// <summary>
/// The normalized result. <see cref="Status"/> and <see cref="Reason"/> keep the official
/// cStat and xMotivo of the answer (FR-021); <see cref="Detail"/> explains a technical
/// outcome and never carries the access key.
/// </summary>
public sealed record SefazResult(
    SefazOutcome Outcome,
    string? Status,
    string? Reason,
    SefazProtocol? Protocol,
    string? ReceiptNumber,
    string? Detail,
    SefazExchange Exchange);
