using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rrf5.Fiscal.Regulatory;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>What the test authorizer answers next (005 T019).</summary>
public enum SefazScenario
{
    Authorized,
    SchemaRejected,
    Denied,
    Duplicate,
    ReceivedAsync,
    Paused,
    TimeoutAfterAuthorization,
    Unreadable,
    SoapFault,
    DigestMismatch,
    OtherKey,
    OtherEnvironment,
    ConsultFound,
    ConsultNotFound,
    ConsultCancelled,
}

/// <summary>
/// In-process SEFAZ-shaped SOAP 1.2 server over HTTPS with a required client certificate,
/// a rewrite of the discovery POC mock (005 T019). Its server certificate comes from the
/// test root, and it only accepts the expected client certificate. It answers with the
/// codes of the frozen MOC 7.0: 104/100, 225, 104/301, 204, 103, 108, 217 and 101. It
/// also simulates a timeout after authorizing, an unreadable body and a SOAP fault.
/// Synthetic data only; it is not an authorizer.
/// </summary>
public sealed partial class SefazTestServer : IAsyncDisposable
{
    private const string Password = "not-a-real-password";
    private const string Nfe = "http://www.portalfiscal.inf.br/nfe";
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _digests = new(StringComparer.Ordinal);

    private SefazTestServer(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; }

    public SefazScenario Scenario { get; set; } = SefazScenario.Authorized;

    /// <summary>How long the timeout scenario holds the answer.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(5);

    public ConcurrentQueue<(string Path, string ContentType, string Body)> Requests { get; } = new();

    public SefazEndpoint Endpoint(string service) => new("TEST", "65", "HOMOLOGATION", service, new Uri(BaseUrl, $"/ws/{service}.asmx"));

    public static async Task<SefazTestServer> StartAsync(X509Certificate2 expectedClient, bool selfSignedServer = false)
    {
        var serverCertificate = X509CertificateLoader.LoadPkcs12(TestCertificates.ServerPkcs12(Password, selfSignedServer), Password);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = (certificate, _, _) => certificate.RawData.AsSpan().SequenceEqual(expectedClient.RawData),
        })));
        var app = builder.Build();
        SefazTestServer? server = null;
        app.MapPost("/ws/{service}.asmx", async (HttpContext http, string service) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(http.RequestAborted);
            server!.Requests.Enqueue((http.Request.Path, http.Request.ContentType ?? string.Empty, body));
            return await server.AnswerAsync(http, service, body);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server = new SefazTestServer(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task<IResult> AnswerAsync(HttpContext http, string service, string body)
    {
        var key = KeyInRequest().Match(body) is { Success: true } byId ? byId.Groups[1].Value : ConsultedKey().Match(body).Groups[1].Value;
        // The consultation has no DigestValue: answer with the one of the document sent earlier.
        var digest = DigestInRequest().Match(body) is { Success: true } d
            ? _digests[key] = d.Groups[1].Value
            : _digests.GetValueOrDefault(key, Synthetic("sintetico-nao-enviado"));
        var authorization = service == SefazEndpoints.Authorization;
        var tpAmb = Scenario == SefazScenario.OtherEnvironment ? "1" : "2";

        string Prot(string cStat, string reason, string? chNFe = null, string? digVal = null) =>
            $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>TEST</verAplic><chNFe>{chNFe ?? key}</chNFe>"
            + $"<dhRecbto>2026-11-12T12:00:01-03:00</dhRecbto><nProt>135260000000001</nProt><digVal>{digVal ?? digest}</digVal>"
            + $"<cStat>{cStat}</cStat><xMotivo>{reason}</xMotivo></infProt></protNFe>";
        string Ret(string inner) =>
            $"<retEnviNFe xmlns=\"{Nfe}\" versao=\"4.00\"><tpAmb>{tpAmb}</tpAmb><verAplic>TEST</verAplic>{inner}</retEnviNFe>";
        string Batch(string cStat, string reason, string rest = "") =>
            Ret($"<cStat>{cStat}</cStat><xMotivo>{reason}</xMotivo><cUF>35</cUF><dhRecbto>2026-11-12T12:00:01-03:00</dhRecbto>{rest}");
        string Cons(string cStat, string reason, string rest = "") =>
            $"<retConsSitNFe xmlns=\"{Nfe}\" versao=\"4.00\"><tpAmb>{tpAmb}</tpAmb><verAplic>TEST</verAplic><cStat>{cStat}</cStat><xMotivo>{reason}</xMotivo><cUF>35</cUF><dhRecbto>2026-11-12T12:00:02-03:00</dhRecbto><chNFe>{key}</chNFe>{rest}</retConsSitNFe>";
        IResult Soap(string inner, int status = 200) => Results.Text(
            $"<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body><nfeResultMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/{service}\">{inner}</nfeResultMsg></soap:Body></soap:Envelope>",
            "application/soap+xml; charset=utf-8", statusCode: status);

        switch (Scenario)
        {
            case SefazScenario.TimeoutAfterAuthorization when authorization:
                await Task.Delay(Delay, http.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
                return Soap(Batch("104", "Lote processado", Prot("100", "Autorizado o uso da NF-e")));
            case SefazScenario.Unreadable:
                return Results.Text("<html><body>Service Unavailable", "text/html", statusCode: 200);
            case SefazScenario.SoapFault:
                return Results.Text("<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body><soap:Fault><soap:Code><soap:Value>soap:Receiver</soap:Value></soap:Code></soap:Fault></soap:Body></soap:Envelope>",
                    "application/soap+xml; charset=utf-8", statusCode: 500);
        }

        if (authorization)
        {
            return Scenario switch
            {
                SefazScenario.SchemaRejected => Soap(Batch("225", "Rejeicao: Falha no Schema XML do lote de NFe")),
                SefazScenario.Denied => Soap(Batch("104", "Lote processado", Prot("301", "Uso Denegado: Irregularidade fiscal do emitente"))),
                SefazScenario.Duplicate => Soap(Batch("104", "Lote processado", Prot("204", "Rejeicao: Duplicidade de NF-e"))),
                SefazScenario.ReceivedAsync => Soap(Batch("103", "Lote recebido com sucesso", "<infRec><nRec>351000000000001</nRec><tMed>1</tMed></infRec>")),
                SefazScenario.Paused => Soap(Batch("108", "Servico Paralisado Momentaneamente (curto prazo)")),
                SefazScenario.DigestMismatch => Soap(Batch("104", "Lote processado", Prot("100", "Autorizado o uso da NF-e", digVal: Synthetic("outro-documento")))),
                SefazScenario.OtherKey => Soap(Batch("104", "Lote processado", Prot("100", "Autorizado o uso da NF-e", chNFe: "35261100000000000000650010000000011123456784"))),
                _ => Soap(Batch("104", "Lote processado", Prot("100", "Autorizado o uso da NF-e"))),
            };
        }

        return Scenario switch
        {
            SefazScenario.ConsultNotFound => Soap(Cons("217", "Rejeicao: NF-e nao consta na base de dados da SEFAZ")),
            SefazScenario.ConsultCancelled => Soap(Cons("101", "Cancelamento de NF-e homologado")),
            _ => Soap(Cons("100", "Autorizado o uso da NF-e", Prot("100", "Autorizado o uso da NF-e"))),
        };
    }

    // A synthetic digVal, built at run time (not a credential).
    private static string Synthetic(string text) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(text));

    [GeneratedRegex("Id=\"NFe([0-9A-Z]{44})\"", RegexOptions.CultureInvariant)]
    private static partial Regex KeyInRequest();

    [GeneratedRegex("<chNFe>([0-9A-Z]{44})</chNFe>", RegexOptions.CultureInvariant)]
    private static partial Regex ConsultedKey();

    [GeneratedRegex("<DigestValue>([^<]+)</DigestValue>", RegexOptions.CultureInvariant)]
    private static partial Regex DigestInRequest();
}
