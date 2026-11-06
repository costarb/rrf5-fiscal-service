using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

// T018/T019: NFeAutorizacao4 (synchronous) and NFeConsultaProtocolo4 for SEFAZ-SP
// NFC-e homologation, with a pluggable transport so the same code path runs
// against canned mock responses. Discovery-only code.
//
// Uncertain-result rule: once the request may have reached the authorizer, any
// transport failure or unparseable answer is RESULT_UNKNOWN. The caller must run
// `consult` with the same signed document before deciding anything; the POC never
// retransmits on its own and never treats this as fiscal contingency.
static partial class SefazClient
{
    public const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";
    const string AuthorizationEndpoint = "https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeAutorizacao4.asmx";
    const string ConsultationEndpoint = "https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeConsultaProtocolo4.asmx";

    public sealed record Exchange(int HttpStatus, string Body);

    public sealed record SignedDocument(string RawNfe, string AccessKey, string DigestValue);

    public delegate Task<Exchange> Transport(string endpoint, string envelope);

    public sealed class UncertainResultException(string reason) : Exception(reason);

    // Reads a signed NFe produced by `sign`, keeping its bytes exactly as signed.
    public static SignedDocument LoadSigned(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        var raw = XmlDeclaration().Replace(text, "", 1).Trim();

        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(raw), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document.Load(reader);
        }
        var ns = Namespaces(document);
        if (document.DocumentElement?.LocalName != "NFe" || document.DocumentElement.NamespaceURI != NfeNamespace)
        {
            throw new InvalidDataException("Root element must be NFe (not nfeProc).");
        }
        if (document.SelectSingleNode("/nfe:NFe/nfe:infNFe/nfe:ide/nfe:tpAmb", ns)?.InnerText != "2")
        {
            throw new InvalidDataException("Only tpAmb=2 (homologation) documents may be sent by the POC.");
        }
        if (document.SelectSingleNode("/nfe:NFe/nfe:infNFe/nfe:ide/nfe:mod", ns)?.InnerText != "65")
        {
            throw new InvalidDataException("Only model 65 (NFC-e) is supported.");
        }
        var digest = document.SelectSingleNode("/nfe:NFe/ds:Signature/ds:SignedInfo/ds:Reference/ds:DigestValue", ns)?.InnerText
                     ?? throw new InvalidDataException("Document is not signed.");
        var id = ((XmlElement)document.SelectSingleNode("/nfe:NFe/nfe:infNFe", ns)!).GetAttribute("Id");
        if (id.Length != 47 || !id.StartsWith("NFe", StringComparison.Ordinal))
        {
            throw new InvalidDataException("infNFe/@Id is not NFe + 44 digits.");
        }
        return new SignedDocument(raw, id[3..], digest);
    }

    public static async Task<(XmlDocument Response, Exchange Exchange, long ElapsedMs)> AuthorizeAsync(
        Transport transport, SignedDocument document, string idLote)
    {
        var envelope = Envelope("NFeAutorizacao4",
            $"<enviNFe xmlns=\"{NfeNamespace}\" versao=\"4.00\"><idLote>{idLote}</idLote><indSinc>1</indSinc>{document.RawNfe}</enviNFe>");
        return await SendAsync(transport, AuthorizationEndpoint, envelope);
    }

    public static async Task<(XmlDocument Response, Exchange Exchange, long ElapsedMs)> ConsultAsync(
        Transport transport, string accessKey)
    {
        var envelope = Envelope("NFeConsultaProtocolo4",
            $"<consSitNFe xmlns=\"{NfeNamespace}\" versao=\"4.00\"><tpAmb>2</tpAmb><xServ>CONSULTAR</xServ><chNFe>{accessKey}</chNFe></consSitNFe>");
        return await SendAsync(transport, ConsultationEndpoint, envelope);
    }

    // Builds nfeProc from the exact signed NFe bytes and the protocol returned.
    public static string BuildNfeProc(SignedDocument document, XmlNode protNfe) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"{NfeNamespace}\" versao=\"4.00\">{document.RawNfe}{protNfe.OuterXml}</nfeProc>";

    public static XmlNamespaceManager Namespaces(XmlDocument document)
    {
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("nfe", NfeNamespace);
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
        return ns;
    }

    // ICP-Brasil root v10 ("Autoridade Certificadora Raiz Brasileira v10"), which
    // anchors the SEFAZ-SP server chain. SHA-256 cross-checked on 2026-11-06 between
    // the ITI download and the Windows trusted root store.
    const string IcpBrasilV10Sha256 = "6e0bff069a26994c15de2c4888cc54af84882e5495b7fbf66be9ccffec7489f6";
    public const string TrustRootVariable = "RRF5_SEFAZ_TRUST_ROOT";

    // Without RRF5_SEFAZ_TRUST_ROOT the OS trust store is used (Windows ships the
    // ICP-Brasil roots; Debian/Ubuntu images do not). With it, server validation
    // stays on but trusts only the pinned ICP-Brasil v10 root. Validation is never
    // disabled.
    public static SocketsHttpHandler CreateHandler(X509Certificate2 clientCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };

        var rootPath = Environment.GetEnvironmentVariable(TrustRootVariable);
        if (!string.IsNullOrEmpty(rootPath))
        {
            var root = X509CertificateLoader.LoadCertificateFromFile(rootPath);
            var fingerprint = root.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
            if (fingerprint != IcpBrasilV10Sha256)
            {
                throw new InvalidDataException($"{TrustRootVariable} is not the pinned ICP-Brasil v10 root (got {fingerprint}).");
            }
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust };
            policy.CustomTrustStore.Add(root);
            handler.SslOptions.CertificateChainPolicy = policy;
        }
        return handler;
    }

    public static Transport HttpTransport(X509Certificate2 certificate) => async (endpoint, envelope) =>
    {
        using var handler = CreateHandler(certificate);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        using var response = await client.PostAsync(endpoint, content);
        return new Exchange((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    };

    // Canned SEFAZ-shaped answers. Codes used: 104/100 (batch processed /
    // authorized), 225 (schema failure), 217 (not found in SEFAZ base) - all
    // observed or catalogued; no other behaviour is simulated.
    public static Transport MockTransport(string scenario, SignedDocument? document) => (endpoint, envelope) =>
    {
        var key = document?.AccessKey ?? EnvelopeKey().Match(envelope).Groups[1].Value;
        var digest = document?.DigestValue ?? "MOCK";
        string Prot(string cStat, string motivo) =>
            $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>MOCK</verAplic><chNFe>{key}</chNFe>" +
            $"<dhRecbto>2026-11-06T12:00:01-03:00</dhRecbto><nProt>135000000000000</nProt><digVal>{digest}</digVal>" +
            $"<cStat>{cStat}</cStat><xMotivo>{motivo}</xMotivo></infProt></protNFe>";
        string Wrap(string service, string inner) =>
            $"<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body><nfeResultMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/{service}\">{inner}</nfeResultMsg></soap:Body></soap:Envelope>";
        string Ret(string inner) =>
            $"<retEnviNFe xmlns=\"{NfeNamespace}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>MOCK</verAplic>{inner}</retEnviNFe>";

        var authorization = endpoint.Contains("NFeAutorizacao4", StringComparison.Ordinal);
        return (scenario, authorization) switch
        {
            ("authorized", true) => Task.FromResult(new Exchange(200, Wrap("NFeAutorizacao4",
                Ret("<cStat>104</cStat><xMotivo>Lote processado</xMotivo><cUF>35</cUF><dhRecbto>2026-11-06T12:00:01-03:00</dhRecbto>" + Prot("100", "Autorizado o uso da NF-e"))))),
            ("schema-rejected", true) => Task.FromResult(new Exchange(200, Wrap("NFeAutorizacao4",
                Ret("<cStat>225</cStat><xMotivo>Rejeicao: Falha no Schema XML do lote de NFe</xMotivo><cUF>35</cUF><dhRecbto>2026-11-06T12:00:01-03:00</dhRecbto>")))),
            ("timeout", true) => Task.FromException<Exchange>(new TaskCanceledException("Mock: no answer within the client timeout.")),
            ("found", false) or ("timeout", false) or ("authorized", false) => Task.FromResult(new Exchange(200, Wrap("NFeConsultaProtocolo4",
                $"<retConsSitNFe xmlns=\"{NfeNamespace}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>MOCK</verAplic><cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo><cUF>35</cUF><chNFe>{key}</chNFe>{Prot("100", "Autorizado o uso da NF-e")}</retConsSitNFe>"))),
            ("not-found", false) => Task.FromResult(new Exchange(200, Wrap("NFeConsultaProtocolo4",
                $"<retConsSitNFe xmlns=\"{NfeNamespace}\" versao=\"4.00\"><tpAmb>2</tpAmb><verAplic>MOCK</verAplic><cStat>217</cStat><xMotivo>Rejeicao: NF-e nao consta na base de dados da SEFAZ</xMotivo><cUF>35</cUF><chNFe>{key}</chNFe></retConsSitNFe>"))),
            _ => throw new ArgumentException($"Unknown mock scenario '{scenario}' for this command.")
        };
    };

    static async Task<(XmlDocument, Exchange, long)> SendAsync(Transport transport, string endpoint, string envelope)
    {
        var stopwatch = Stopwatch.StartNew();
        Exchange exchange;
        try
        {
            exchange = await transport(endpoint, envelope);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new UncertainResultException($"{exception.GetType().Name}: {exception.Message}");
        }
        stopwatch.Stop();

        var response = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(exchange.Body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            response.Load(reader);
        }
        catch (XmlException)
        {
            throw new UncertainResultException($"HTTP {exchange.HttpStatus} returned a non-XML body.");
        }
        return (response, exchange, stopwatch.ElapsedMilliseconds);
    }

    static string Envelope(string service, string payload) =>
        "<soap12:Envelope xmlns:soap12=\"http://www.w3.org/2003/05/soap-envelope\"><soap12:Body>" +
        $"<nfeDadosMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/{service}\">{payload}</nfeDadosMsg>" +
        "</soap12:Body></soap12:Envelope>";

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [GeneratedRegex(@"^\s*<\?xml[^>]*\?>")]
    private static partial Regex XmlDeclaration();

    [GeneratedRegex(@"<chNFe>(\d{44})</chNFe>")]
    private static partial Regex EnvelopeKey();
}
