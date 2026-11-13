using System.Security.Cryptography;
using System.Text;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>QR Code content and URLs of an authorized document (contract <c>PrintData</c>).</summary>
public sealed record PrintData(string QrCode, string QrCodeUrl, string ConsultationUrl, string AccessKey);

/// <summary>
/// Reads what an ERP prints (005 T027): the stored <c>nfeProc</c>, the DANFE NFC-e built
/// only from it, and the print data. Only an <c>AUTHORIZED</c> document has them. The
/// bytes are re-hashed on every read against the hash recorded at authorization, and the
/// QR Code and consultation URLs must match the catalog of the authorizer, so a tampered
/// or misrouted document is never printed.
/// </summary>
public sealed class DocumentPrinting(IUnitOfWorkFactory database, IArtifactStore store, ISefazEndpointCatalog endpoints, PaymentMethods paymentMethods)
{
    private const string Model = "65";

    public async Task<byte[]> GetProcAsync(Caller caller, Guid documentId, CancellationToken cancellationToken) =>
        (await LoadAsync(caller, documentId, cancellationToken).ConfigureAwait(false)).Bytes;

    public async Task<PrintData> GetPrintDataAsync(Caller caller, Guid documentId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(caller, documentId, cancellationToken).ConfigureAwait(false);
        var nfce = loaded.Parse();
        return new PrintData(nfce.QrCode, loaded.QrCodeUrl, nfce.ConsultationUrl, nfce.AccessKey);
    }

    public async Task<DanfeResult> GetDanfeAsync(Caller caller, Guid documentId, DanfePaper paper, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(caller, documentId, cancellationToken).ConfigureAwait(false);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(loaded.IssuedAt, loaded.TimeZone).DateTime);
        return Danfe.Render(loaded.Parse(), new DanfeOptions(paper, loaded.TimeZone, code => paymentMethods.Resolve(code, date)?.Description));
    }

    private sealed record Loaded(byte[] Bytes, string QrCodeUrl, string ConsultationUrl, TimeZoneInfo TimeZone, DateTimeOffset IssuedAt)
    {
        public AuthorizedNfce Parse() => AuthorizedNfce.Parse(new UTF8Encoding(false, true).GetString(Bytes), QrCodeUrl, ConsultationUrl);
    }

    private async Task<Loaded> LoadAsync(Caller caller, Guid documentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.DocumentsRead) && !caller.Scopes.Contains(Scopes.DocumentsIssue))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }

        DocumentRecord document;
        string authorizer;
        string timeZone;
        string environment;
        await using (var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false))
        {
            var found = await uow.Documents.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (found is null
                || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(found.EstablishmentId))
            {
                throw new DomainException(ErrorCodes.NotFound);
            }

            if (found.State != DocumentStates.Authorized || found.ProcXmlObjectKey is null || found.ProcXmlSha256 is null || found.IssuedAt is null)
            {
                throw new DomainException(ErrorCodes.DocumentNotAuthorized);
            }

            var configuration = await uow.Issuer.GetActiveConfigurationAsync(found.EnvironmentId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.DocumentNotAuthorized, "The environment has no active configuration to print with.");
            var environmentRecord = (await uow.Issuer.GetEnvironmentAsync(found.EnvironmentId, cancellationToken).ConfigureAwait(false))!;
            document = found;
            authorizer = configuration.Authorizer;
            timeZone = configuration.Timezone;
            environment = environmentRecord.Environment;
        }

        var bytes = await store.GetAsync(document.ProcXmlObjectKey, document.ProcXmlObjectVersion, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.ProcXmlSha256, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ArtifactIntegrity);
        }

        var qr = endpoints.Resolve(authorizer, Model, environment, SefazEndpoints.QrCode);
        var consultation = endpoints.Resolve(authorizer, Model, environment, SefazEndpoints.KeyConsultation);
        if (qr is null || consultation is null)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The QR Code and consultation URLs of the authorizer are not catalogued.");
        }

        // Same text the generator wrote (DocumentProcessor): catalog URLs without a trailing slash.
        return new Loaded(bytes, qr.Url.ToString().TrimEnd('/'), consultation.Url.ToString().TrimEnd('/'),
            TimeZoneInfo.FindSystemTimeZoneById(timeZone), document.IssuedAt.Value);
    }
}
