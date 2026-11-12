using System.Collections.Frozen;
using System.Text.Json;

namespace Rrf5.Fiscal.Regulatory;

/// <summary>One catalogued web service of an authorizer.</summary>
public sealed record SefazEndpoint(string Authorizer, string Model, string Environment, string Service, Uri Url);

/// <summary>
/// SEFAZ web service endpoints by authorizer, model, environment and service, embedded as
/// <c>regulatory/sefaz-endpoints.json</c> (005 T017, ADR-007 item 2). Endpoints come
/// only from this catalog, copied from the dated evidence of <c>sp-nfce-services</c>; only
/// SP homologation is catalogued. A missing entry returns null, and the caller fails
/// closed: there is no default host and no derivation of one URL from another.
/// </summary>
public sealed class SefazEndpoints
{
    public const string Authorization = "NFeAutorizacao4";
    public const string ProtocolQuery = "NFeConsultaProtocolo4";
    public const string QrCode = "NFCeQrCode";
    public const string KeyConsultation = "NFCeConsultaChave";

    private readonly FrozenDictionary<(string, string, string, string), SefazEndpoint> _byKey;

    private SefazEndpoints(FrozenDictionary<(string, string, string, string), SefazEndpoint> byKey) => _byKey = byKey;

    public IReadOnlyCollection<SefazEndpoint> All => _byKey.Values;

    public static SefazEndpoints LoadEmbedded()
    {
        using var stream = typeof(SefazEndpoints).Assembly.GetManifestResourceStream("regulatory/sefaz-endpoints.json")
            ?? throw new InvalidOperationException("The embedded SEFAZ endpoint catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        var endpoints = document.RootElement.GetProperty("endpoints").EnumerateArray().Select(e =>
        {
            var url = new Uri(e.GetProperty("url").GetString()!, UriKind.Absolute);
            if (url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Every catalogued SEFAZ endpoint must use HTTPS.");
            }

            return new SefazEndpoint(
                e.GetProperty("authorizer").GetString()!,
                e.GetProperty("model").GetString()!,
                e.GetProperty("environment").GetString()!,
                e.GetProperty("service").GetString()!,
                url);
        });
        return new SefazEndpoints(endpoints.ToFrozenDictionary(e => (e.Authorizer, e.Model, e.Environment, e.Service)));
    }

    /// <summary>The catalogued endpoint, or null when there is none (fail closed).</summary>
    public SefazEndpoint? Resolve(string authorizer, string model, string environment, string service) =>
        _byKey.TryGetValue((authorizer, model, environment, service), out var endpoint) ? endpoint : null;
}
