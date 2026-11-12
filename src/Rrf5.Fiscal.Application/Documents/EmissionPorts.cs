using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>Server trust for an authorizer's TLS endpoint (005 T005, ADR-007 item 3).</summary>
public interface ISefazTrust
{
    Task<X509ChainPolicy> PolicyForAsync(Authorizer authorizer, CancellationToken cancellationToken);
}

/// <summary>Endpoints of the authorizer (ADR-007 item 2). A null answer fails closed.</summary>
public interface ISefazEndpointCatalog
{
    SefazEndpoint? Resolve(string authorizer, string model, string environment, string service);
}

/// <summary>Validation against the XSD set of a resolved schema package (005 T014, FE-002); returns the errors.</summary>
public interface INfceSchemaProvider
{
    Task<IReadOnlyList<string>> ValidateAsync(SchemaPackage package, string xml, CancellationToken cancellationToken);
}

/// <summary>Only the authorizer's pinned ICP-Brasil root, from the hash-verified bundle.</summary>
public sealed class IcpBrasilSefazTrust(ICertificateTrustProvider bundle) : ISefazTrust
{
    public async Task<X509ChainPolicy> PolicyForAsync(Authorizer authorizer, CancellationToken cancellationToken) =>
        SefazTlsTrust.PolicyFor(authorizer, await bundle.GetAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary>The embedded endpoint catalog (<c>regulatory/sefaz-endpoints.json</c>).</summary>
public sealed class EmbeddedSefazEndpointCatalog(SefazEndpoints endpoints) : ISefazEndpointCatalog
{
    public SefazEndpoint? Resolve(string authorizer, string model, string environment, string service) =>
        endpoints.Resolve(authorizer, model, environment, service);
}

/// <summary>Reads the package ZIP from the artifact store (hash re-verified on every read) and caches the compiled set per hash.</summary>
public sealed class StoredNfceSchemaProvider(ArtifactService artifacts) : INfceSchemaProvider
{
    private readonly ConcurrentDictionary<string, NfceSchema> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> ValidateAsync(SchemaPackage package, string xml, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!_cache.TryGetValue(package.ArtifactSha256, out var schema))
        {
            var zip = await artifacts.ReadAsync(package.ArtifactSha256, cancellationToken).ConfigureAwait(false);
            schema = _cache.GetOrAdd(package.ArtifactSha256, NfceSchema.FromPackage(zip));
        }

        return schema.Validate(xml);
    }
}
