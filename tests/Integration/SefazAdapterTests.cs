using System.Security.Cryptography.X509Certificates;
using Rrf5.Fiscal.Adapters.Sefaz;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>
/// 005 T017 to T019: the SEFAZ adapter against the test SOAP server, over real mutual TLS.
/// <list type="bullet">
/// <item>AUTHORIZED only with cStat 100 and the same chNFe and digVal.</item>
/// <item>Official codes are kept, and every outcome follows the frozen MOC 7.0.</item>
/// <item>A failure after the request left is uncertain and is resolved by consulting the key.</item>
/// <item>A server outside the pinned root is never reached.</item>
/// </list>
/// Throwaway "NOT AN A1" certificates and synthetic documents only.
/// </summary>
public sealed class SefazAdapterTests : IAsyncLifetime
{
    private const string Password = "not-a-real-password";
    private const string Cnpj = "12ABC34501DE35";
    private X509Certificate2 _client = null!;
    private SefazTestServer _server = null!;
    private SefazNfceClient _sefaz = null!;

    public async Task InitializeAsync()
    {
        _client = X509CertificateLoader.LoadPkcs12(TestCertificates.Pkcs12(Password, Cnpj), Password);
        _server = await SefazTestServer.StartAsync(_client);
        _sefaz = CreateClient(Connection());
    }

    public async Task DisposeAsync()
    {
        _sefaz.Dispose();
        await _server.DisposeAsync();
        _client.Dispose();
    }

    [Fact]
    public async Task AuthorizationSendsTheSignedBytesAndAcceptsOnlyTheSameKeyAndDigest()
    {
        var (xml, key, digest) = Signed();
        var result = await Authorize(xml, key, digest);

        Assert.Equal(SefazOutcome.Authorized, result.Outcome);
        Assert.Equal("100", result.Status);
        Assert.Equal(key, result.Protocol!.AccessKey);
        Assert.Equal(digest, result.Protocol.DigestValue);
        Assert.Equal("135260000000001", result.Protocol.ProtocolNumber);
        Assert.StartsWith("<protNFe", result.Protocol.Xml, StringComparison.Ordinal);
        Assert.Equal(200, result.Exchange.HttpStatus);

        var (path, contentType, body) = Assert.Single(_server.Requests);
        Assert.Equal("/ws/NFeAutorizacao4.asmx", path);
        Assert.Equal("application/soap+xml; charset=utf-8", contentType);
        Assert.Contains("<idLote>42</idLote><indSinc>1</indSinc><NFe", body, StringComparison.Ordinal);
        Assert.Contains(xml[xml.IndexOf("<NFe", StringComparison.Ordinal)..], body, StringComparison.Ordinal); // byte for byte
        Assert.Equal(body, result.Exchange.Request);
    }

    [Theory]
    [InlineData(SefazScenario.SchemaRejected, SefazOutcome.Rejected, "225")]
    [InlineData(SefazScenario.Denied, SefazOutcome.Denied, "301")]
    [InlineData(SefazScenario.Duplicate, SefazOutcome.Uncertain, "204")]
    [InlineData(SefazScenario.ReceivedAsync, SefazOutcome.Received, "103")]
    [InlineData(SefazScenario.Paused, SefazOutcome.ServiceUnavailable, "108")]
    [InlineData(SefazScenario.DigestMismatch, SefazOutcome.Mismatch, "100")]
    [InlineData(SefazScenario.OtherKey, SefazOutcome.Uncertain, "100")]
    [InlineData(SefazScenario.OtherEnvironment, SefazOutcome.Unexpected, "104")]
    [InlineData(SefazScenario.Unreadable, SefazOutcome.Uncertain, null)]
    [InlineData(SefazScenario.SoapFault, SefazOutcome.Uncertain, null)]
    public async Task EveryAnswerIsClassifiedByTheMoc(SefazScenario scenario, SefazOutcome expected, string? status)
    {
        _server.Scenario = scenario;
        var (xml, key, digest) = Signed();
        var result = await Authorize(xml, key, digest);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.Status);
        if (expected != SefazOutcome.Authorized)
        {
            Assert.True(result.Protocol is null || result.Outcome is SefazOutcome.Denied or SefazOutcome.Mismatch);
        }

        if (scenario == SefazScenario.ReceivedAsync)
        {
            Assert.Equal("351000000000001", result.ReceiptNumber);
        }

        Assert.DoesNotContain(key, result.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutAfterAuthorizationIsUncertainAndTheConsultationResolvesIt()
    {
        _server.Scenario = SefazScenario.TimeoutAfterAuthorization;
        var (xml, key, digest) = Signed();
        var sent = await Authorize(xml, key, digest);
        Assert.Equal(SefazOutcome.Uncertain, sent.Outcome);
        Assert.Null(sent.Exchange.HttpStatus);
        Assert.Contains("response timeout", sent.Detail, StringComparison.Ordinal);

        _server.Scenario = SefazScenario.ConsultFound;
        var consulted = await _sefaz.ConsultAsync(_server.Endpoint(SefazEndpoints.ProtocolQuery), key, digest, "2", CancellationToken.None);
        Assert.Equal(SefazOutcome.Authorized, consulted.Outcome);
        Assert.Equal(digest, consulted.Protocol!.DigestValue);
        Assert.Contains($"<chNFe>{key}</chNFe>", _server.Requests.Last().Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SefazScenario.ConsultNotFound, SefazOutcome.NotFound, "217")]
    [InlineData(SefazScenario.ConsultCancelled, SefazOutcome.Unexpected, "101")]
    [InlineData(SefazScenario.ConsultFound, SefazOutcome.Mismatch, "100")]
    public async Task ConsultationOutcomes(SefazScenario scenario, SefazOutcome expected, string status)
    {
        // ConsultFound without a previous send: the server answers with another digVal.
        _server.Scenario = scenario;
        var (_, key, digest) = Signed();
        var result = await _sefaz.ConsultAsync(_server.Endpoint(SefazEndpoints.ProtocolQuery), key, digest, "2", CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task AServerOutsideThePinnedRootIsNeverReached()
    {
        await using var impostor = await SefazTestServer.StartAsync(_client, selfSignedServer: true);
        var (xml, key, digest) = Signed();
        var result = await _sefaz.AuthorizeAsync(impostor.Endpoint(SefazEndpoints.Authorization), xml, key, digest, "2", 1, CancellationToken.None);

        Assert.Equal(SefazOutcome.NotSent, result.Outcome);
        Assert.Empty(impostor.Requests);
    }

    [Fact]
    public async Task AClosedPortIsNotSent()
    {
        var closed = new SefazEndpoint("TEST", "65", "HOMOLOGATION", SefazEndpoints.Authorization, new Uri("https://127.0.0.1:1/ws/NFeAutorizacao4.asmx"));
        var (xml, key, digest) = Signed();
        var result = await _sefaz.AuthorizeAsync(closed, xml, key, digest, "2", 1, CancellationToken.None);
        Assert.Equal(SefazOutcome.NotSent, result.Outcome);
    }

    [Fact]
    public async Task TheClientRefusesWhatItMustNotSend()
    {
        var (xml, key, digest) = Signed();
        var http = new SefazEndpoint("TEST", "65", "HOMOLOGATION", SefazEndpoints.Authorization, new Uri("http://127.0.0.1/ws/NFeAutorizacao4.asmx"));
        await Assert.ThrowsAsync<ArgumentException>(() => _sefaz.AuthorizeAsync(http, xml, key, digest, "2", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _sefaz.AuthorizeAsync(_server.Endpoint(SefazEndpoints.ProtocolQuery), xml, key, digest, "2", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _sefaz.AuthorizeAsync(_server.Endpoint(SefazEndpoints.Authorization), xml, key[..43] + "0", digest, "2", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _sefaz.AuthorizeAsync(_server.Endpoint(SefazEndpoints.Authorization), xml, key, digest, "3", 1, CancellationToken.None));
        Assert.Empty(_server.Requests);
    }

    // Disposed in DisposeAsync (xUnit IAsyncLifetime).
    private static SefazNfceClient CreateClient(SefazConnection connection) =>
        new(connection, new SefazClientOptions { ResponseTimeout = TimeSpan.FromSeconds(2) });

    private Task<SefazResult> Authorize(string xml, string key, string digest) =>
        _sefaz.AuthorizeAsync(_server.Endpoint(SefazEndpoints.Authorization), xml, key, digest, "2", 42, CancellationToken.None);

    private SefazConnection Connection()
    {
        var trust = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        trust.CustomTrustStore.Add(TestCertificates.RootCertificate());
        return new SefazConnection(_client, [], trust);
    }

    private (string Xml, string Key, string Digest) Signed()
    {
        var taxes = new NfceTaxes("0", "102", "99", 0m, "99", 0m);
        var generated = NfceGenerator.Generate(new NfceDocument(
            new NfceIdentification("35", AccessKey.NewNumericCode(7), "VENDA", 1, 7, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(-3)), "2", "3550308", "RRF5-TEST"),
            new NfceIssuer(Cnpj, "ISENTO", "EMITENTE SINTETICO",
                new NfceAddress("RUA SINTETICA", "0", "BAIRRO", "3550308", "Sao Paulo", "SP", "01001000"), "1"),
            null,
            [new NfceItem("A-1", "SEM GTIN", "LANCHE", "21069090", "5101", "UN", 1m, 10m, 10m, 0m, 0m, taxes)],
            [new NfcePayment("01", null, 10m, null)],
            0m,
            "https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode",
            "https://www.homologacao.nfce.fazenda.sp.gov.br/consulta"));
        var signed = NfceSigner.Sign(generated.Xml, _client);
        return (signed.Xml, generated.AccessKey, signed.DigestValue);
    }
}
