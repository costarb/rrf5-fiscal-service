using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Adapters.Sefaz;

/// <summary>
/// What the worker hands the client: the A1 opened from the vault with the chain shipped in
/// its PKCS#12, and the server trust of the authorizer (<c>SefazTlsTrust.PolicyFor</c>, the
/// pinned ICP-Brasil root). The client never consults the operating system store.
/// </summary>
public sealed record SefazConnection(X509Certificate2 ClientCertificate, X509Certificate2Collection ClientChain, X509ChainPolicy ServerTrust);

/// <summary>Timeouts and limits (ADR-007 item 5).</summary>
public sealed record SefazClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxResponseBytes { get; init; } = 5_000_000;
}

/// <summary>
/// NFC-e client for <c>NFeAutorizacao4</c> (synchronous, <c>indSinc</c> 1) and
/// <c>NFeConsultaProtocolo4</c>, SOAP 1.2 over mutual TLS (005 T017, ADR-007). The SOAP shape
/// is the one SEFAZ-SP authorized in the discovery POC.
/// <para>
/// The client tells two failures apart. If the request body never started to go out
/// (connection or TLS handshake failure), the result is <see cref="SefazOutcome.NotSent"/>
/// and a technical retry is safe. Once the body started, any failure is
/// <see cref="SefazOutcome.Uncertain"/>: the caller consults by key and never resends on its
/// own. A technical retry is never an official contingency (AGENTS.md).
/// </para>
/// </summary>
public sealed partial class SefazNfceClient : IDisposable
{
    private const string SoapNamespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string WsdlNamespace = "http://www.portalfiscal.inf.br/nfe/wsdl/";
    private readonly HttpClient _http;
    private readonly SefazClientOptions _options;

    public SefazNfceClient(SefazConnection connection, SefazClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new SefazClientOptions();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // The chain is built offline from the PKCS#12 so the handshake never waits on
                // AIA downloads (discovery POC finding).
                ClientCertificateContext = SslStreamCertificateContext.Create(connection.ClientCertificate, connection.ClientChain, offline: true),
                // Custom root trust: only the authorizer's pinned root; the name check stays on.
                CertificateChainPolicy = connection.ServerTrust,
            },
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = _options.MaxResponseBytes,
        };
    }

    /// <summary>
    /// Sends one signed NFC-e in a synchronous batch. <paramref name="signedNfe"/> is the
    /// exact output of the signer; its bytes go out unchanged, without the XML declaration.
    /// </summary>
    public async Task<SefazResult> AuthorizeAsync(
        SefazEndpoint endpoint, string signedNfe, string accessKey, string digestValue, string tpAmb, long batchId, CancellationToken cancellationToken)
    {
        RequireEndpoint(endpoint, SefazEndpoints.Authorization);
        ArgumentNullException.ThrowIfNull(signedNfe);
        RequireKey(accessKey);
        RequireTpAmb(tpAmb);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchId, 999_999_999_999_999); // idLote: N 1-15
        var nfe = XmlDeclaration().Replace(signedNfe, string.Empty, 1);
        if (!nfe.StartsWith("<NFe", StringComparison.Ordinal))
        {
            throw new ArgumentException("signedNfe must be a signed NFe element.", nameof(signedNfe));
        }

        var payload = $"<enviNFe xmlns=\"{SefazResponses.NfeNamespace}\" versao=\"4.00\"><idLote>{batchId}</idLote><indSinc>1</indSinc>{nfe}</enviNFe>";
        var (exchange, technical) = await SendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
        return Join(technical ?? SefazResponses.ReadAuthorization(exchange.HttpStatus, exchange.Response, accessKey, digestValue, tpAmb), exchange);
    }

    /// <summary>Consults the situation of the document with this key (FE-010).</summary>
    public async Task<SefazResult> ConsultAsync(
        SefazEndpoint endpoint, string accessKey, string digestValue, string tpAmb, CancellationToken cancellationToken)
    {
        RequireEndpoint(endpoint, SefazEndpoints.ProtocolQuery);
        RequireKey(accessKey);
        RequireTpAmb(tpAmb);
        var payload = $"<consSitNFe xmlns=\"{SefazResponses.NfeNamespace}\" versao=\"4.00\"><tpAmb>{tpAmb}</tpAmb><xServ>CONSULTAR</xServ><chNFe>{accessKey}</chNFe></consSitNFe>";
        var (exchange, technical) = await SendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
        return Join(technical ?? SefazResponses.ReadConsultation(exchange.HttpStatus, exchange.Response, accessKey, digestValue, tpAmb), exchange);
    }

    public void Dispose() => _http.Dispose();

    private async Task<(SefazExchange Exchange, SefazReading? Technical)> SendAsync(SefazEndpoint endpoint, string payload, CancellationToken cancellationToken)
    {
        var envelope = $"<soap12:Envelope xmlns:soap12=\"{SoapNamespace}\"><soap12:Body>"
            + $"<nfeDadosMsg xmlns=\"{WsdlNamespace}{endpoint.Service}\">{payload}</nfeDadosMsg>"
            + "</soap12:Body></soap12:Envelope>";
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var content = new TrackedContent(Encoding.UTF8.GetBytes(envelope));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = content, Version = HttpVersion.Version11 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ResponseTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return (new SefazExchange(endpoint.Service, endpoint.Url, started, stopwatch.ElapsedMilliseconds, envelope, (int)response.StatusCode, body), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or AuthenticationException)
        {
            var exchange = new SefazExchange(endpoint.Service, endpoint.Url, started, stopwatch.ElapsedMilliseconds, envelope, null, null);
            if (!content.Started)
            {
                // Nothing left the process: a caller cancellation is just a cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                return (exchange, new SefazReading(SefazOutcome.NotSent, null, null, null, null, Describe(exception)));
            }

            var detail = exception is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? "No answer within the response timeout after the request was sent."
                : "The exchange failed after the request was sent: " + Describe(exception);
            return (exchange, new SefazReading(SefazOutcome.Uncertain, null, null, null, null, detail));
        }
    }

    // Type names only: exception messages may carry hosts or payload fragments.
    private static string Describe(Exception exception)
    {
        var names = new List<string>();
        for (var current = exception; current is not null && names.Count < 4; current = current.InnerException)
        {
            names.Add(current.GetType().Name);
        }

        return string.Join(" <- ", names);
    }

    private static SefazResult Join(SefazReading reading, SefazExchange exchange) =>
        new(reading.Outcome, reading.Status, reading.Reason, reading.Protocol, reading.ReceiptNumber, reading.Detail, exchange);

    private static void RequireEndpoint(SefazEndpoint endpoint, string service)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Service != service || endpoint.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"An HTTPS {service} endpoint is required.", nameof(endpoint));
        }
    }

    private static void RequireKey(string accessKey)
    {
        if (!AccessKey.IsWellFormed(accessKey))
        {
            throw new ArgumentException("The access key is not well formed.", nameof(accessKey));
        }
    }

    private static void RequireTpAmb(string tpAmb)
    {
        if (tpAmb is not ("1" or "2"))
        {
            throw new ArgumentException("tpAmb must be 1 or 2.", nameof(tpAmb));
        }
    }

    [GeneratedRegex(@"^\s*<\?xml[^>]*\?>", RegexOptions.CultureInvariant)]
    private static partial Regex XmlDeclaration();

    /// <summary>Request body that records whether it started to go out on the wire.</summary>
    private sealed class TrackedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public TrackedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        }

        public bool Started { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started = true;
            await stream.WriteAsync(_bytes, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}
