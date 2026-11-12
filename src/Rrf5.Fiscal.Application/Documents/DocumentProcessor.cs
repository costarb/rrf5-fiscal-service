using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rrf5.Fiscal.Adapters.Sefaz;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Observability;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Domain.Issuer;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>What the outbox does with the message after one run.</summary>
public sealed record ProcessResult(bool Done, TimeSpan RetryAfter)
{
    public static ProcessResult Finished { get; } = new(true, TimeSpan.Zero);

    public static ProcessResult Retry(TimeSpan after) => new(false, after);
}

/// <summary>Emission settings of the worker.</summary>
public sealed record EmissionOptions
{
    public SefazClientOptions Sefaz { get; init; } = new();

    /// <summary>Delays between consultations of a document waiting on SEFAZ; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> ConsultBackoff { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>Consultations without a definite answer before the alert (FE-010, T022).</summary>
    public int ConsultAlertAfter { get; init; } = 5;

    public string ApplicationVersion { get; init; } = "RRF5-FISCAL-0.5";
}

/// <summary>
/// The worker side of the emission (005 T016, T020, T022 and T023; ADR-005 items 3 and 6).
/// Each step locks the document, checks the expected state and commits its transition.
/// The database guards every edge, so a duplicate or late message finds another state and
/// does nothing.
/// <list type="number">
/// <item><c>READY_TO_SIGN</c>: checks that the rule versions are still in force, reserves the
/// number right before signing and binds it (FE-008); <c>SIGNING</c>.</item>
/// <item>Generates, validates against the resolved XSD, signs with the A1 opened from the
/// vault, verifies, validates the signed document and stores it write-once; <c>SIGNED</c>,
/// then <c>QUEUED</c>.</item>
/// <item><c>SENDING</c>; one synchronous authorization. 100 with the same key and digest:
/// the <c>nfeProc</c> is stored, then <c>AUTHORIZED</c>. An official refusal:
/// <c>REJECTED</c>. Not sent: back to <c>QUEUED</c>. Anything else: <c>WAITING_SEFAZ</c>.</item>
/// <item><c>WAITING_SEFAZ</c>: consults by key before any resend. 100: <c>AUTHORIZED</c>. 217:
/// <c>QUEUED</c>, then the same signed bytes are sent again. Otherwise it waits with
/// backoff and alerts. A technical retry is never an official contingency.</item>
/// </list>
/// </summary>
public sealed partial class DocumentProcessor(
    IUnitOfWorkFactory database,
    ICertificateVault vault,
    ISefazTrust trust,
    ISefazEndpointCatalog endpoints,
    INfceSchemaProvider schemas,
    IArtifactStore store,
    TimeProvider clock,
    EmissionOptions options,
    ILogger<DocumentProcessor> logger)
{
    private const string Model = "65";
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<ProcessResult> ProcessAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        for (var step = 0; step < 8; step++)
        {
            var document = await ReadAsync(tenantId, documentId, cancellationToken).ConfigureAwait(false);
            if (document is null || DocumentStates.IsTerminal(document.State))
            {
                return ProcessResult.Finished;
            }

            ProcessResult? result = document.State switch
            {
                DocumentStates.ReadyToSign => await SignAsync(tenantId, documentId, cancellationToken).ConfigureAwait(false),
                DocumentStates.Error when document.SignedAt is null => await ResetAsync(tenantId, documentId, DocumentStates.ReadyToSign, cancellationToken).ConfigureAwait(false),
                DocumentStates.Error or DocumentStates.SignedState => await ResetAsync(tenantId, documentId, DocumentStates.Queued, cancellationToken).ConfigureAwait(false),
                DocumentStates.Signing => await FailSigningAsync(tenantId, documentId, "interrupted", cancellationToken).ConfigureAwait(false),
                DocumentStates.Queued => await SendAsync(tenantId, documentId, cancellationToken).ConfigureAwait(false),
                DocumentStates.Sending => await InterruptedSendAsync(tenantId, documentId, cancellationToken).ConfigureAwait(false),
                DocumentStates.WaitingSefaz => await ConsultAsync(tenantId, documentId, cancellationToken).ConfigureAwait(false),
                _ => ProcessResult.Finished,
            };
            if (result is not null)
            {
                return result;
            }
        }

        return ProcessResult.Retry(TimeSpan.FromSeconds(5));
    }

    // ----- 1 and 2: number, sign, store -------------------------------------------------

    private async Task<ProcessResult?> SignAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        using var activity = EmissionTelemetry.Start("sign", tenantId, documentId);
        Prepared prepared;
        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.ReadyToSign)
            {
                return null;
            }

            var issuedAt = clock.GetUtcNow();
            var failure = await PrepareAsync(uow, document, issuedAt, cancellationToken).ConfigureAwait(false);
            if (failure.Prepared is null)
            {
                await uow.Documents.SetStateAsync(documentId, DocumentStates.Error, cancellationToken).ConfigureAwait(false);
                await AuditAsync(uow, tenantId, "document.error", documentId, failure.Reason!, cancellationToken).ConfigureAwait(false);
                await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                LogError(logger, documentId, failure.Reason!);
                return ProcessResult.Retry(TimeSpan.FromMinutes(1));
            }

            prepared = failure.Prepared;
            await uow.Documents.SetStateAsync(documentId, DocumentStates.Signing, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        SignedFields signedFields;
        try
        {
            using var a1 = await OpenA1Async(tenantId, prepared.Certificate, cancellationToken).ConfigureAwait(false);
            var generated = NfceGenerator.Generate(prepared.Nfce);
            if (generated.AccessKey != prepared.AccessKey)
            {
                throw new InvalidOperationException("The generated access key differs from the bound one.");
            }

            var signed = NfceSigner.Sign(generated.Xml, a1.Leaf);
            var schemaErrors = await schemas.ValidateAsync(prepared.Schema, signed.Xml, cancellationToken).ConfigureAwait(false);
            if (schemaErrors.Count != 0)
            {
                // FE-002: never send a document the resolved XSD refuses (the errors are not logged: they quote content).
                throw new InvalidOperationException($"The signed document fails the XSD of {prepared.Schema.CatalogReferenceId} ({schemaErrors.Count} errors).");
            }

            var bytes = Utf8.GetBytes(signed.Xml);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var stored = await PutVerifiedAsync(sha, bytes, cancellationToken).ConfigureAwait(false);
            signedFields = new SignedFields(clock.GetUtcNow(), signed.DigestValue, sha, prepared.Certificate.FingerprintSha256,
                prepared.Schema.CatalogReferenceId, stored.ObjectKey, stored.Version);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailSigningAsync(tenantId, documentId, exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }

        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.Signing)
            {
                return null;
            }

            await uow.Documents.MarkSignedAsync(documentId, signedFields, cancellationToken).ConfigureAwait(false);
            await uow.Documents.SetStateAsync(documentId, DocumentStates.Queued, cancellationToken).ConfigureAwait(false);
            await AuditAsync(uow, tenantId, "document.signed", documentId, null, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            EmissionTelemetry.Transition(DocumentStates.Queued);
        }

        return null;
    }

    private sealed record Prepared(NfceDocument Nfce, string AccessKey, CertificateRecord Certificate, SchemaPackage Schema);

    private sealed record Preparation(Prepared? Prepared, string? Reason);

    private async Task<Preparation> PrepareAsync(IUnitOfWork uow, DocumentRecord document, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(issuedAt, SaoPaulo).DateTime);
        var snapshot = document.TaxRuleSnapshot!.Value;
        var environment = (await uow.Issuer.GetEnvironmentAsync(document.EnvironmentId, cancellationToken).ConfigureAwait(false))!;
        var establishment = (await uow.Issuer.GetEstablishmentAsync(document.EstablishmentId, cancellationToken).ConfigureAwait(false))!;
        var company = (await uow.Issuer.GetCompanyAsync(establishment.CompanyId, cancellationToken).ConfigureAwait(false))!;
        var configuration = await uow.Issuer.GetActiveConfigurationAsync(environment.Id, cancellationToken).ConfigureAwait(false);
        if (environment.Environment != "HOMOLOGATION")
        {
            return new Preparation(null, "production emission is not enabled");
        }

        if (configuration is null || Authorizers.ByUf.Values.FirstOrDefault(a => a.Code == configuration.Authorizer) is not { } authorizer)
        {
            return new Preparation(null, "no active configuration with a catalogued authorizer");
        }

        if (string.IsNullOrWhiteSpace(establishment.StateRegistration))
        {
            return new Preparation(null, "the establishment has no state registration (IE)");
        }

        var certificate = (await uow.Certificates.ListAsync(environment.Id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.Status == "ACTIVE" && c.NotBefore <= issuedAt && issuedAt <= c.NotAfter);
        if (certificate is null)
        {
            return new Preparation(null, "no ACTIVE certificate within its validity");
        }

        var schema = SchemaResolver.Resolve(await uow.Schemas.ListAsync(cancellationToken).ConfigureAwait(false), Model, authorizer.Uf, environment.Environment, date);
        if (schema is null)
        {
            return new Preparation(null, "no catalogued schema package applies on the issue date");
        }

        var qr = endpoints.Resolve(authorizer.Code, Model, environment.Environment, SefazEndpoints.QrCode);
        var consultation = endpoints.Resolve(authorizer.Code, Model, environment.Environment, SefazEndpoints.KeyConsultation);
        if (qr is null || consultation is null)
        {
            return new Preparation(null, "the QR Code and consultation URLs are not catalogued");
        }

        // The approved versions captured at issue must still be the ones in force (FR-016, ADR-015 item 7).
        var rules = new Dictionary<Guid, JsonElement>();
        foreach (var rule in snapshot.GetProperty("rules").EnumerateArray())
        {
            var versionId = rule.GetProperty("versionId").GetGuid();
            var inForce = await uow.TaxRules.FindInForceAsync(rule.GetProperty("ruleId").GetGuid(), date, cancellationToken).ConfigureAwait(false);
            if (inForce?.Id != versionId)
            {
                return new Preparation(null, "an approved rule version of the document is no longer in force");
            }

            rules[versionId] = rule.GetProperty("payload");
        }

        var seriesId = document.SeriesId ?? snapshot.GetProperty("seriesId").GetGuid();
        var series = (await uow.Issuer.GetSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false))!;
        string accessKey;
        if (document.Number is null)
        {
            var allocation = await uow.Issuer.AllocateAsync(seriesId, cancellationToken).ConfigureAwait(false);
            if (allocation.Reservation is not { } reservation)
            {
                return new Preparation(null, "no number could be reserved (" + allocation.Failure + ")");
            }

            var cnf = AccessKey.NewNumericCode(reservation.Number);
            accessKey = AccessKey.Build(Federation.IbgeCodeByUf[establishment.Uf], issuedAt.ToOffset(SaoPaulo.GetUtcOffset(issuedAt)), establishment.TaxId,
                Model, series.Series, reservation.Number, "1", cnf);
            await uow.Documents.BindNumberAsync(document.Id, reservation.Id, seriesId, reservation.Number, accessKey, issuedAt, cancellationToken).ConfigureAwait(false);
            await AuditAsync(uow, document.TenantId, "series.reserve_number", document.Id,
                reservation.Number.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            document = document with { Number = reservation.Number, AccessKey = accessKey, IssuedAt = issuedAt };
        }
        else
        {
            // A retry keeps the number and key; the issue time moves only within the key's month (AAMM).
            accessKey = document.AccessKey!;
            var local = issuedAt.ToOffset(SaoPaulo.GetUtcOffset(issuedAt));
            if (local.ToString("yyMM", CultureInfo.InvariantCulture) == accessKey[2..6])
            {
                await uow.Documents.RefreshIssuedAtAsync(document.Id, issuedAt, cancellationToken).ConfigureAwait(false);
                document = document with { IssuedAt = issuedAt };
            }
        }

        var items = await uow.Documents.ListItemsAsync(document.Id, cancellationToken).ConfigureAwait(false);
        var payments = await uow.Documents.ListPaymentsAsync(document.Id, cancellationToken).ConfigureAwait(false);
        var address = establishment.Address;
        var nfce = new NfceDocument(
            new NfceIdentification(
                Federation.IbgeCodeByUf[establishment.Uf], accessKey[35..43], snapshot.GetProperty("operation").GetString()!, series.Series,
                document.Number!.Value, document.IssuedAt!.Value.ToOffset(SaoPaulo.GetUtcOffset(document.IssuedAt.Value)), "2",
                establishment.MunicipalityCode, options.ApplicationVersion),
            new NfceIssuer(establishment.TaxId, establishment.StateRegistration!, company.LegalName,
                new NfceAddress(address.GetProperty("street").GetString()!, address.GetProperty("number").GetString()!,
                    address.GetProperty("district").GetString()!, establishment.MunicipalityCode, address.GetProperty("municipalityName").GetString()!,
                    establishment.Uf, address.GetProperty("postalCode").GetString()!),
                establishment.Crt.ToString(CultureInfo.InvariantCulture)),
            document.ConsumerCpf is { } cpf ? new NfceConsumer(cpf, document.ConsumerName) : null,
            items.Select(i =>
            {
                var payload = rules[i.TaxRuleVersionId];
                var pis = payload.GetProperty("pis");
                var cofins = payload.GetProperty("cofins");
                return new NfceItem(i.ProductCode, i.Gtin, i.Description, i.Ncm, i.Cfop, i.Unit, i.Quantity, i.UnitPrice, i.LineValue, i.Discount, i.Surcharge,
                    new NfceTaxes(i.Origin, i.Csosn, pis.GetProperty("cst").GetString()!, decimal.Parse(pis.GetProperty("ratePercent").GetString()!, CultureInfo.InvariantCulture),
                        cofins.GetProperty("cst").GetString()!, decimal.Parse(cofins.GetProperty("ratePercent").GetString()!, CultureInfo.InvariantCulture)),
                    i.Cest);
            }).ToList(),
            payments.Select(p => new NfcePayment(p.TPag, p.Description, p.Amount,
                p.CardIntegration is { } integration ? new NfceCard(integration, p.AcquirerCnpj, p.CardBrand, p.AuthorizationCode) : null)).ToList(),
            document.Change,
            qr.Url.ToString().TrimEnd('/'),
            consultation.Url.ToString().TrimEnd('/'));
        return new Preparation(new Prepared(nfce, accessKey, certificate, schema), null);
    }

    private async Task<ProcessResult> FailSigningAsync(Guid tenantId, Guid documentId, string reason, CancellationToken cancellationToken)
    {
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document?.State == DocumentStates.Signing)
        {
            await uow.Documents.SetStateAsync(documentId, DocumentStates.Error, cancellationToken).ConfigureAwait(false);
            await AuditAsync(uow, tenantId, "document.error", documentId, "signing: " + reason, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            EmissionTelemetry.Transition(DocumentStates.Error);
            LogError(logger, documentId, "signing: " + reason);
        }

        return ProcessResult.Retry(TimeSpan.FromSeconds(30));
    }

    private async Task<ProcessResult?> ResetAsync(Guid tenantId, Guid documentId, string state, CancellationToken cancellationToken)
    {
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document is not null && document.State is DocumentStates.Error or DocumentStates.SignedState)
        {
            await uow.Documents.SetStateAsync(documentId, state, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // ----- 3: send ------------------------------------------------------------------------

    private async Task<ProcessResult?> SendAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        using var activity = EmissionTelemetry.Start("send", tenantId, documentId);
        Target target;
        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.Queued)
            {
                return null;
            }

            target = await TargetAsync(uow, document, SefazEndpoints.Authorization, cancellationToken).ConfigureAwait(false);
            await uow.Documents.SetStateAsync(documentId, DocumentStates.Sending, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var signedXml = await ReadSentXmlAsync(target.Document, cancellationToken).ConfigureAwait(false);
        SefazResult result;
        using (var a1 = await OpenA1Async(tenantId, target.Certificate, cancellationToken).ConfigureAwait(false))
        using (var client = new SefazNfceClient(new SefazConnection(a1.Leaf, a1.Chain, await trust.PolicyForAsync(target.Authorizer, cancellationToken).ConfigureAwait(false)), options.Sefaz))
        {
            var batchId = clock.GetUtcNow().ToUnixTimeMilliseconds();
            result = await client.AuthorizeAsync(target.Endpoint, signedXml, target.Document.AccessKey!, target.Document.DigVal!, "2", batchId, cancellationToken).ConfigureAwait(false);
        }

        activity?.SetTag("rrf5.code", result.Status ?? result.Outcome.ToString());
        EmissionTelemetry.Exchange("AUTHORIZE", result.Outcome.ToString(), result.Exchange.ElapsedMilliseconds);

        var proc = result.Outcome == SefazOutcome.Authorized ? await StoreProcAsync(signedXml, result.Protocol!, cancellationToken).ConfigureAwait(false) : null;
        var exchange = await StoreExchangeAsync(result, cancellationToken).ConfigureAwait(false);
        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.Sending)
            {
                return null;
            }

            await RecordAsync(uow, documentId, "AUTHORIZE", result, exchange, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SefazOutcome.Authorized:
                    await uow.Documents.MarkAuthorizedAsync(documentId, proc!.Sha256, proc.ObjectKey, proc.Version, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    await AuditAsync(uow, tenantId, "document.authorized", documentId, null, cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    EmissionTelemetry.Transition(DocumentStates.Authorized);
                    return ProcessResult.Finished;
                case SefazOutcome.Rejected:
                    await uow.Documents.SetStateAsync(documentId, DocumentStates.Rejected, cancellationToken).ConfigureAwait(false);
                    await AuditAsync(uow, tenantId, "document.rejected", documentId, result.Status, cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    EmissionTelemetry.Transition(DocumentStates.Rejected);
                    return ProcessResult.Finished;
                case SefazOutcome.NotSent:
                    await uow.Documents.SetStateAsync(documentId, DocumentStates.Queued, cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    LogNotSent(logger, documentId, result.Detail ?? string.Empty);
                    return ProcessResult.Retry(TimeSpan.FromSeconds(30));
                default:
                    // FE-010: after the request may have left, doubt is WAITING_SEFAZ and a consultation.
                    await uow.Documents.SetStateAsync(documentId, DocumentStates.WaitingSefaz, cancellationToken).ConfigureAwait(false);
                    await AuditAsync(uow, tenantId, "document.waiting_sefaz", documentId, result.Outcome.ToString(), cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    EmissionTelemetry.Transition(DocumentStates.WaitingSefaz);
                    LogWaiting(logger, documentId, result.Outcome.ToString(), result.Status ?? "-");
                    return ProcessResult.Retry(options.ConsultBackoff[0]);
            }
        }
    }

    private async Task<ProcessResult> InterruptedSendAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        // A crash between SENDING and the answer: the request may have left. Never resend blindly.
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (document?.State == DocumentStates.Sending)
        {
            await uow.Documents.SetStateAsync(documentId, DocumentStates.WaitingSefaz, cancellationToken).ConfigureAwait(false);
            await AuditAsync(uow, tenantId, "document.waiting_sefaz", documentId, "interrupted", cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return ProcessResult.Retry(TimeSpan.Zero);
    }

    // ----- 4: consult before any resend ---------------------------------------------------

    private async Task<ProcessResult?> ConsultAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        using var activity = EmissionTelemetry.Start("consult", tenantId, documentId);
        Target target;
        int consultations;
        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.WaitingSefaz)
            {
                return null;
            }

            target = await TargetAsync(uow, document, SefazEndpoints.ProtocolQuery, cancellationToken).ConfigureAwait(false);
            consultations = await uow.Documents.CountExchangesAsync(documentId, "CONSULT", cancellationToken).ConfigureAwait(false);
        }

        SefazResult result;
        using (var a1 = await OpenA1Async(tenantId, target.Certificate, cancellationToken).ConfigureAwait(false))
        using (var client = new SefazNfceClient(new SefazConnection(a1.Leaf, a1.Chain, await trust.PolicyForAsync(target.Authorizer, cancellationToken).ConfigureAwait(false)), options.Sefaz))
        {
            result = await client.ConsultAsync(target.Endpoint, target.Document.AccessKey!, target.Document.DigVal!, "2", cancellationToken).ConfigureAwait(false);
        }

        activity?.SetTag("rrf5.code", result.Status ?? result.Outcome.ToString());
        EmissionTelemetry.Exchange("CONSULT", result.Outcome.ToString(), result.Exchange.ElapsedMilliseconds);

        var proc = result.Outcome == SefazOutcome.Authorized
            ? await StoreProcAsync(await ReadSentXmlAsync(target.Document, cancellationToken).ConfigureAwait(false), result.Protocol!, cancellationToken).ConfigureAwait(false)
            : null;
        var exchange = await StoreExchangeAsync(result, cancellationToken).ConfigureAwait(false);
        await using (var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var document = await uow.Documents.LockAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (document?.State != DocumentStates.WaitingSefaz)
            {
                return null;
            }

            await RecordAsync(uow, documentId, "CONSULT", result, exchange, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SefazOutcome.Authorized:
                    await uow.Documents.MarkAuthorizedAsync(documentId, proc!.Sha256, proc.ObjectKey, proc.Version, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    await AuditAsync(uow, tenantId, "document.authorized", documentId, "consultation", cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    EmissionTelemetry.Transition(DocumentStates.Authorized);
                    return ProcessResult.Finished;
                case SefazOutcome.NotFound:
                    // 217 after the last send: the database allows the resend (migration 0011).
                    await uow.Documents.SetStateAsync(documentId, DocumentStates.Queued, cancellationToken).ConfigureAwait(false);
                    await AuditAsync(uow, tenantId, "document.resend", documentId, "217", cancellationToken).ConfigureAwait(false);
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                default:
                    await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
                    var attempt = consultations + 1;
                    var manual = result.Outcome is SefazOutcome.Mismatch or SefazOutcome.Unexpected or SefazOutcome.Denied;
                    if (manual || attempt >= options.ConsultAlertAfter)
                    {
                        LogConsultAlert(logger, documentId, result.Outcome.ToString(), result.Status ?? "-", attempt);
                    }

                    var backoff = options.ConsultBackoff[Math.Min(attempt, options.ConsultBackoff.Count - 1)];
                    return ProcessResult.Retry(manual ? TimeSpan.FromHours(1) : backoff);
            }
        }
    }

    // ----- shared -------------------------------------------------------------------------

    private sealed record Target(DocumentRecord Document, Authorizer Authorizer, SefazEndpoint Endpoint, CertificateRecord Certificate);

    private async Task<Target> TargetAsync(IUnitOfWork uow, DocumentRecord document, string service, CancellationToken cancellationToken)
    {
        var environment = (await uow.Issuer.GetEnvironmentAsync(document.EnvironmentId, cancellationToken).ConfigureAwait(false))!;
        var configuration = await uow.Issuer.GetActiveConfigurationAsync(environment.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The environment has no active configuration.");
        var authorizer = Authorizers.ByUf.Values.First(a => a.Code == configuration.Authorizer);
        var endpoint = endpoints.Resolve(authorizer.Code, Model, environment.Environment, service)
            ?? throw new InvalidOperationException("The endpoint is not catalogued (fail closed).");
        var now = clock.GetUtcNow();
        var certificate = (await uow.Certificates.ListAsync(environment.Id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => c.Status == "ACTIVE" && c.NotBefore <= now && now <= c.NotAfter)
            ?? throw new InvalidOperationException("No ACTIVE certificate within its validity.");
        return new Target(document, authorizer, endpoint, certificate);
    }

    private sealed class OpenedA1(X509Certificate2 leaf, X509Certificate2Collection chain) : IDisposable
    {
        public X509Certificate2 Leaf { get; } = leaf;

        public X509Certificate2Collection Chain { get; } = chain;

        public void Dispose()
        {
            Leaf.Dispose();
            foreach (var certificate in Chain)
            {
                certificate.Dispose();
            }
        }
    }

    // Only the worker can open the A1 (policy rrf5-worker). Windows keeps the key in a
    // temporary persisted container because SChannel cannot use ephemeral keys for client TLS.
    private async Task<OpenedA1> OpenA1Async(Guid tenantId, CertificateRecord certificate, CancellationToken cancellationToken)
    {
        var material = await vault.OpenCertificateAsync(tenantId, certificate.Id, certificate.VaultRef, cancellationToken).ConfigureAwait(false);
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var all = X509CertificateLoader.LoadPkcs12Collection(material.Pkcs12, material.Password, flags);
        var leaf = all.FirstOrDefault(c => c.HasPrivateKey && Convert.ToHexStringLower(SHA256.HashData(c.RawData)) == certificate.FingerprintSha256);
        if (leaf is null)
        {
            foreach (var c in all)
            {
                c.Dispose();
            }

            throw new CryptographicException("The vault material does not hold the expected certificate.");
        }

        var chain = new X509Certificate2Collection();
        foreach (var c in all)
        {
            if (!ReferenceEquals(c, leaf))
            {
                chain.Add(c);
            }
        }

        return new OpenedA1(leaf, chain);
    }

    private async Task<string> ReadSentXmlAsync(DocumentRecord document, CancellationToken cancellationToken)
    {
        var bytes = await store.GetAsync(document.SentXmlObjectKey!, document.SentXmlObjectVersion, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != document.SignedXmlSha256)
        {
            throw new CryptographicException("The stored signed XML does not match its recorded hash.");
        }

        return Utf8.GetString(bytes);
    }

    private sealed record StoredBlob(string Sha256, string ObjectKey, string? Version);

    private async Task<StoredObject> PutVerifiedAsync(string sha, byte[] bytes, CancellationToken cancellationToken)
    {
        var stored = await store.PutAsync(sha, bytes, cancellationToken).ConfigureAwait(false);
        var echoed = await store.GetAsync(stored.ObjectKey, stored.Version, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(echoed)) != sha)
        {
            throw new CryptographicException("The object store did not keep the bytes that were written.");
        }

        return stored;
    }

    // nfeProc: the exact signed NFe bytes and the protocol element as received (T023).
    private async Task<StoredBlob> StoreProcAsync(string signedXml, SefazProtocol protocol, CancellationToken cancellationToken)
    {
        var nfe = signedXml.StartsWith("<?xml", StringComparison.Ordinal) ? signedXml[(signedXml.IndexOf("?>", StringComparison.Ordinal) + 2)..] : signedXml;
        var proc = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"{SefazResponses.NfeNamespace}\" versao=\"4.00\">{nfe}{protocol.Xml}</nfeProc>";
        var bytes = Utf8.GetBytes(proc);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var stored = await PutVerifiedAsync(sha, bytes, cancellationToken).ConfigureAwait(false);
        return new StoredBlob(sha, stored.ObjectKey, stored.Version);
    }

    private sealed record StoredExchange(StoredBlob Request, StoredBlob? Response);

    private async Task<StoredExchange> StoreExchangeAsync(SefazResult result, CancellationToken cancellationToken)
    {
        async Task<StoredBlob> Put(string text)
        {
            var bytes = Utf8.GetBytes(text);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var stored = await store.PutAsync(sha, bytes, cancellationToken).ConfigureAwait(false);
            return new StoredBlob(sha, stored.ObjectKey, stored.Version);
        }

        var request = await Put(result.Exchange.Request).ConfigureAwait(false);
        var response = result.Exchange.Response is { Length: > 0 } body ? await Put(body).ConfigureAwait(false) : null;
        return new StoredExchange(request, response);
    }

    private static async Task RecordAsync(IUnitOfWork uow, Guid documentId, string operation, SefazResult result, StoredExchange stored, CancellationToken cancellationToken)
    {
        var answered = result.Status is not null && stored.Response is not null;
        var outcome = result.Outcome switch
        {
            SefazOutcome.NotSent => "NOT_SENT",
            _ when answered => "ANSWERED",
            _ when result.Exchange.HttpStatus is not null => "UNREADABLE",
            _ when result.Detail?.Contains("timeout", StringComparison.Ordinal) == true => "TIMEOUT",
            _ => "TRANSPORT_ERROR",
        };
        var exchangeId = Guid.NewGuid();
        var finished = result.Exchange.StartedAt.AddMilliseconds(result.Exchange.ElapsedMilliseconds);
        await uow.Documents.InsertExchangeAsync(new NewExchange(
            exchangeId, documentId, operation, outcome, answered ? int.Parse(result.Status!, CultureInfo.InvariantCulture) : null,
            stored.Request.Sha256, stored.Request.ObjectKey, stored.Request.Version, stored.Response?.Sha256, stored.Response?.ObjectKey,
            stored.Response?.Version, result.Exchange.HttpStatus, result.Exchange.StartedAt, finished), cancellationToken).ConfigureAwait(false);

        if (!answered)
        {
            return;
        }

        // Official answers are kept apart from technical failures (FR-021).
        if (result.Protocol is { } p && int.TryParse(p.Status, CultureInfo.InvariantCulture, out var protocolStatus))
        {
            await uow.Documents.InsertProtocolAsync(new NewProtocol(exchangeId, documentId, operation == "CONSULT" ? "CONSULTATION" : "PROTOCOL",
                protocolStatus, Truncate(p.Reason), p.ProtocolNumber, p.AccessKey, p.DigestValue,
                DateTimeOffset.TryParse(p.ReceivedAt, CultureInfo.InvariantCulture, out var received) ? received.ToUniversalTime() : finished), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await uow.Documents.InsertProtocolAsync(new NewProtocol(exchangeId, documentId, operation == "CONSULT" ? "CONSULTATION" : "BATCH",
                int.Parse(result.Status!, CultureInfo.InvariantCulture), Truncate(result.Reason ?? "-"), null, null, null, finished), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Truncate(string text) => text.Length <= 255 ? (text.Length == 0 ? "-" : text) : text[..255];

    private async Task<DocumentRecord?> ReadAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        return await uow.Documents.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
    }

    private static Task AuditAsync(IUnitOfWork uow, Guid tenantId, string action, Guid documentId, string? code, CancellationToken cancellationToken) =>
        uow.Audit.AppendAsync(new AuditEntry(tenantId, Principal.System, action, "fiscal_document", documentId.ToString("D"), AuditOutcome.Succeeded,
            "worker-" + documentId.ToString("N"), code is null ? null : new Dictionary<string, string?> { ["code"] = code }), cancellationToken);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Document {resourceId} moved to ERROR: {reason}")]
    private static partial void LogError(ILogger logger, Guid resourceId, string reason);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Document {resourceId} was not sent ({reason}); it will be retried")]
    private static partial void LogNotSent(ILogger logger, Guid resourceId, string reason);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Document {resourceId} is WAITING_SEFAZ after {outcome} (cStat {code}); it will be consulted by key")]
    private static partial void LogWaiting(ILogger logger, Guid resourceId, string outcome, string code);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Error, Message = "Document {resourceId} still has no definite answer after {count} consultations ({outcome}, cStat {code}); manual review")]
    private static partial void LogConsultAlert(ILogger logger, Guid resourceId, string outcome, string code, int count);
}
