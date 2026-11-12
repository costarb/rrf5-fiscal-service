using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Infrastructure.Observability;
using Npgsql;
using Rrf5.Fiscal.Adapters.Sefaz;
using Rrf5.Fiscal.Application.Documents;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Infrastructure;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>
/// 005 Phase 6 end to end: the API issues, and the worker (its own rrf5_worker login,
/// vault and store) numbers, signs, stores, sends to the test SOAP server and finalizes.
/// <list type="bullet">
/// <item>Asynchronous and synchronous issue (T020, T021).</item>
/// <item>The uncertain result is resolved by consulting before any resend (T022).</item>
/// <item>The sent XML and the nfeProc are stored write-once and hash-verified (T023).</item>
/// <item>Idempotency never reserves a second number (T024).</item>
/// </list>
/// With the official 010g ZIP on the machine the signed XML is validated by the real XSD;
/// without it (CI), the XSD step is the only one replaced.
/// Synthetic data and a throwaway "NOT AN A1" only.
/// </summary>
public sealed class EmissionTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private const string Password = "not-a-real-password";
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private readonly StackFixture _stack;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private SefazTestServer _sefaz = null!;
    private ServiceProvider _worker = null!;
    private X509Certificate2 _a1 = null!;
    private string _manager = null!;
    private Guid _tenant;
    private Guid _environment;
    private Guid _series;
    private string _nature = null!;
    private StringWriter _apiLogs = null!;
    private StringWriter _workerLogs = null!;
    private readonly List<Activity> _spans = [];

    public EmissionTests(StackFixture stack) => _stack = stack;

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);

    public async Task InitializeAsync()
    {
        (_apiLogs, _workerLogs) = (NewLog(), NewLog());
        _factory = _stack.CreateFactory(new Dictionary<string, string?> { ["Documents:SyncTimeoutSeconds"] = "20" })
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(new LogSink(_apiLogs))));
        _client = _factory.CreateClient();
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Emission tenant");
        _tenant = tenant.TenantId;
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var manager = await TenantSetup.ApplicationAsync(_client, admin, "Emission manager",
            Scopes.IssuerWrite, Scopes.CertificatesManage, Scopes.FiscalRulesWrite, Scopes.DocumentsIssue, Scopes.DocumentsRead);
        var approver = await TenantSetup.ApplicationAsync(_client, admin, "Approver", Scopes.CertificatesApprove, Scopes.FiscalRulesApprove);
        _manager = await ApiClient.TokenAsync(_client, manager.ClientId, manager.Secret);
        var approverToken = await ApiClient.TokenAsync(_client, approver.ClientId, approver.Secret);

        var root = IssuerSetup.NewRoot();
        Guid establishment;
        (_, establishment, _environment) = await IssuerSetup.HomologationIssuerAsync(_client, _manager, root);
        await Expect(HttpStatusCode.NoContent, HttpMethod.Put, $"/v1/applications/{approver.Id}/establishments/{establishment}", admin);
        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/environments/{_environment}/configuration", _manager,
            new { timezone = "America/Sao_Paulo", authorizer = "SP", catalogVersion = "1.0" });
        var (_, series) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/series", _manager, new { model = "65", series = 1 });
        _series = series.GetProperty("id").GetGuid();

        var pkcs12 = TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root));
        _a1 = X509CertificateLoader.LoadPkcs12(pkcs12, Password);
        var (_, certificate) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/certificates", _manager,
            new { pkcs12 = Convert.ToBase64String(pkcs12), password = Password });
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/certificates/{certificate.GetProperty("id").GetGuid()}/approve", approverToken);

        // Operation nature: own production 21069090 with CFOP 5101 / CSOSN 102 / PIS-COFINS 99 (synthetic rule).
        _nature = $"EMI-{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        var (_, profile) = await Send(HttpMethod.Post, "/v1/tax-profiles", _manager, new { code = _nature, name = "Emission nature" });
        var (_, rule) = await Send(HttpMethod.Post, $"/v1/tax-profiles/{profile.GetProperty("id").GetGuid()}/rules", _manager,
            new { model = "65", uf = "SP", operation = "VENDA", itemClass = "ncm-21069090" });
        var (_, version) = await Send(HttpMethod.Post, $"/v1/tax-rules/{rule.GetProperty("id").GetGuid()}/versions", _manager, new
        {
            payload = new
            {
                crt = "1",
                cfop = "5101",
                icms = new { group = "ICMSSN102", csosn = "102" },
                pis = new { group = "PISOutr", cst = "99", ratePercent = "0.0000" },
                cofins = new { group = "COFINSOutr", cst = "99", ratePercent = "0.0000" },
                ibsCbs = new { send = false, basis = "test" },
                vTotTrib = new { send = false, basis = "test" },
            },
            validFrom = Today,
            validTo = (DateOnly?)null,
            sourceRefs = new[] { new { kind = "test", reference = "synthetic rule for automated tests", date = (string?)null } },
        });
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{version.GetProperty("id").GetGuid()}/submit", _manager);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{version.GetProperty("id").GetGuid()}/approve", approverToken);
        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/environments/{_environment}/payment-methods", _manager,
            new { methods = new Dictionary<string, string> { ["CASH"] = "01", ["PIX"] = "17" } });

        _sefaz = await SefazTestServer.StartAsync(_a1);
        _worker = WorkerServices();
        _ = _worker.GetRequiredService<TracerProvider>();
        await _worker.GetRequiredService<Rrf5.Fiscal.Infrastructure.Storage.S3ArtifactStore>().EnsureBucketAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _worker.DisposeAsync();
        await _sefaz.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
        _a1.Dispose();
        _apiLogs.Dispose();
        _workerLogs.Dispose();
    }

    [Fact]
    public async Task AsyncIssueIsNumberedSignedSentAndAuthorized()
    {
        var (accepted, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal("READY_TO_SIGN", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("number").ValueKind); // no number before the worker signs
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/v1/documents/{id:D}", accepted.Headers.Location!.OriginalString);

        await DrainAsync();
        var (_, document) = await Send(HttpMethod.Get, $"/v1/documents/{id}", _manager);
        Assert.Equal("AUTHORIZED", document.GetProperty("state").GetString());
        Assert.True(document.GetProperty("terminal").GetBoolean());
        Assert.Equal(100, document.GetProperty("authorization").GetProperty("cStat").GetInt32());
        Assert.Equal("135260000000001", document.GetProperty("authorization").GetProperty("protocol").GetString());
        var key = document.GetProperty("accessKey").GetString()!;
        Assert.True(AccessKey.IsWellFormed(key));

        // T023: the sent XML and the nfeProc are stored, hash-verified and pinned to their version.
        var (sentKey, sentVersion, sentSha, procKey, procVersion, procSha, reservation) = await StoredAsync(id);
        var store = _worker.GetRequiredService<IArtifactStore>();
        var sent = await store.GetAsync(sentKey, sentVersion, CancellationToken.None);
        Assert.Equal(sentSha, Convert.ToHexStringLower(SHA256.HashData(sent)));
        var proc = Encoding.UTF8.GetString(await store.GetAsync(procKey, procVersion, CancellationToken.None));
        Assert.Equal(procSha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(proc))));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"http://www.portalfiscal.inf.br/nfe\" versao=\"4.00\"><NFe", proc, StringComparison.Ordinal);
        var signedNfe = Encoding.UTF8.GetString(sent);
        signedNfe = signedNfe[signedNfe.IndexOf("<NFe", StringComparison.Ordinal)..];
        Assert.Contains(signedNfe, proc, StringComparison.Ordinal);
        Assert.Contains($"<chNFe>{key}</chNFe>", proc, StringComparison.Ordinal);
        Assert.Equal("BOUND", reservation);

        // The signed bytes on the wire are the stored ones; CFOP and CSOSN came from the ERP and match the rule.
        var request = _sefaz.Requests.Single(r => r.Path.EndsWith("NFeAutorizacao4.asmx", StringComparison.Ordinal)).Body;
        Assert.Contains(signedNfe, request, StringComparison.Ordinal);
        Assert.Contains("<CFOP>5101</CFOP>", request, StringComparison.Ordinal);
        Assert.Contains("<CST>99</CST>", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncIssueWaitsForTheResult()
    {
        using var stop = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!await Outbox.RunOneAsync(_tenant, CancellationToken.None))
                {
                    await Task.Delay(100, CancellationToken.None);
                }
            }
        });

        var (created, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents", _manager, Sale());
        await stop.CancelAsync();
        await worker;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("AUTHORIZED", body.GetProperty("state").GetString());
        Assert.Equal("SYNC", body.GetProperty("emissionMode").GetString());
    }

    [Fact]
    public async Task SyncIssueDegradesTo202WhenTheTimeoutExpires()
    {
        await using var factory = _stack.CreateFactory(new Dictionary<string, string?> { ["Documents:SyncTimeoutSeconds"] = "1" });
        using var client = factory.CreateClient();
        var (response, body) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/environments/{_environment}/documents", _manager, Sale(), ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(body.GetProperty("terminal").GetBoolean());
        Assert.NotNull(response.Headers.RetryAfter);
        await DrainAsync();
    }

    [Fact]
    public async Task SameKeyReturnsTheSameDocumentAndNeverASecondNumber()
    {
        var key = ApiClient.NewKey();
        var sale = Sale();
        var (first, firstBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, sale, key);
        await DrainAsync();
        var next = await NextNumberAsync();

        var (replay, replayBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, sale, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), replayBody.GetProperty("id").GetGuid());
        Assert.Equal("AUTHORIZED", replayBody.GetProperty("state").GetString());

        var other = Sale();
        var (conflict, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, other, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency.conflict", problem.GetProperty("code").GetString());
        await DrainAsync();
        Assert.Equal(next, await NextNumberAsync());
    }

    [Fact]
    public async Task AnOfficialRefusalIsRejectedWithTheOfficialCode()
    {
        _sefaz.Scenario = SefazScenario.SchemaRejected;
        var (_, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        await DrainAsync();
        var (_, document) = await Send(HttpMethod.Get, $"/v1/documents/{body.GetProperty("id").GetGuid()}", _manager);
        Assert.Equal("REJECTED", document.GetProperty("state").GetString());
        Assert.Equal(225, document.GetProperty("rejection").GetProperty("cStat").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("authorization").ValueKind);
    }

    [Fact]
    public async Task ATimeoutIsResolvedByConsultationWithoutResending()
    {
        _sefaz.Scenario = SefazScenario.TimeoutAfterAuthorization;
        var (_, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        var id = body.GetProperty("id").GetGuid();
        Assert.True(await Outbox.RunOneAsync(_tenant, CancellationToken.None));
        var (_, waiting) = await Send(HttpMethod.Get, $"/v1/documents/{id}", _manager);
        Assert.Equal("WAITING_SEFAZ", waiting.GetProperty("state").GetString());

        _sefaz.Scenario = SefazScenario.ConsultFound;
        await DrainAsync();
        var (_, document) = await Send(HttpMethod.Get, $"/v1/documents/{id}", _manager);
        Assert.Equal("AUTHORIZED", document.GetProperty("state").GetString());
        Assert.Single(_sefaz.Requests, r => r.Path.EndsWith("NFeAutorizacao4.asmx", StringComparison.Ordinal) && r.Body.Contains(document.GetProperty("accessKey").GetString()!, StringComparison.Ordinal));
        Assert.Equal(["AUTHORIZE:TIMEOUT", "CONSULT:ANSWERED"], await ExchangesAsync(id));
    }

    [Fact]
    public async Task NotFoundOnConsultationAllowsResendingTheSameSignedBytes()
    {
        _sefaz.Scenario = SefazScenario.TimeoutAfterAuthorization;
        var (_, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        var id = body.GetProperty("id").GetGuid();
        Assert.True(await Outbox.RunOneAsync(_tenant, CancellationToken.None));

        // The consultation answers 217; the same signed document goes out again and is authorized.
        _sefaz.Scenario = SefazScenario.ConsultNotFound;
        await DrainAsync();
        var (_, document) = await Send(HttpMethod.Get, $"/v1/documents/{id}", _manager);
        Assert.Equal("AUTHORIZED", document.GetProperty("state").GetString());
        var sends = _sefaz.Requests.Where(r => r.Path.EndsWith("NFeAutorizacao4.asmx", StringComparison.Ordinal)
            && r.Body.Contains(document.GetProperty("accessKey").GetString()!, StringComparison.Ordinal)).Select(r => NfePart(r.Body)).ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal(sends[0], sends[1]);
        Assert.Equal(["AUTHORIZE:TIMEOUT", "CONSULT:ANSWERED", "AUTHORIZE:ANSWERED"], await ExchangesAsync(id));
    }

    [Fact]
    public async Task APrevalidationFailureStoresNothing()
    {
        var sale = Sale();
        ((Dictionary<string, object?>)((object[])sale["items"]!)[0])["cfop"] = "5102"; // the rule says 5101
        var (response, problem) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, sale);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("document.invalid", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "document.tax_mismatch");
        await using var owner = await _stack.OpenOwnerAsync();
        await using var count = new NpgsqlCommand("select count(*) from fiscal_document where external_id = $1", owner);
        count.Parameters.Add(new NpgsqlParameter { Value = sale["externalId"]! });
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TelemetryNeverCarriesTheAccessKeyCpfDigestOrXml()
    {
        // One authorized document and one resolved by consultation after a timeout.
        var (_, first) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        await DrainAsync();
        _sefaz.Scenario = SefazScenario.TimeoutAfterAuthorization;
        var (_, second) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/async", _manager, Sale());
        Assert.True(await Outbox.RunOneAsync(_tenant, CancellationToken.None));
        _sefaz.Scenario = SefazScenario.ConsultFound;
        await DrainAsync();

        var secrets = new List<string> { "12345678909", "<NFe", "<?xml", "infNFe", "nfeProc", "<Signature" };
        foreach (var id in new[] { first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid() })
        {
            var (_, document) = await Send(HttpMethod.Get, $"/v1/documents/{id}", _manager);
            Assert.Equal("AUTHORIZED", document.GetProperty("state").GetString());
            secrets.Add(document.GetProperty("accessKey").GetString()!);
            secrets.Add(await DigestAsync(id));
        }

        await Task.Delay(200);
        var logs = _apiLogs + Environment.NewLine + _workerLogs;
        var spans = SpanSnapshot();

        // Not vacuous: the stages were logged and traced.
        Assert.Contains("\"resourceId\"", _workerLogs.ToString(), StringComparison.Ordinal);
        Assert.Contains(spans, s => s.DisplayName == "document.send");
        Assert.Contains(spans, s => s.DisplayName == "document.consult" && Equals(s.GetTagItem("rrf5.code"), "100"));

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
            foreach (var span in spans)
            {
                Assert.DoesNotContain(secret, span.DisplayName, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, span.StatusDescription ?? string.Empty, StringComparison.Ordinal);
                Assert.All(span.TagObjects.Concat(span.Events.SelectMany(e => e.Tags)), tag =>
                    Assert.DoesNotContain(secret, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal));
            }
        }
    }

    private List<Activity> SpanSnapshot()
    {
        lock (_spans)
        {
            return [.. _spans];
        }
    }

    private async Task<string> DigestAsync(Guid id)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select dig_val from fiscal_document where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // Disposed in DisposeAsync (xUnit IAsyncLifetime).
    private static StringWriter NewLog() => new(System.Globalization.CultureInfo.InvariantCulture);

    private DocumentOutbox Outbox => _worker.GetRequiredService<DocumentOutbox>();

    private async Task DrainAsync()
    {
        for (var i = 0; i < 20 && await Outbox.RunOneAsync(_tenant, CancellationToken.None); i++)
        {
        }

        _sefaz.Scenario = SefazScenario.Authorized;
    }

    private ServiceProvider WorkerServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Worker"] = _stack.WorkerConnectionString,
            ["OpenBao:Address"] = _stack.BaoAddress,
            ["OpenBao:Token"] = _stack.BaoToken,
            ["ObjectStorage:ServiceUrl"] = Environment.GetEnvironmentVariable("RRF5_TEST_S3") ?? "http://127.0.0.1:58333/",
            ["ObjectStorage:AccessKey"] = "local-dev-only",
            ["ObjectStorage:SecretKey"] = "local-dev-only",
            ["ObjectStorage:Bucket"] = "rrf5-test-artifacts",
        }).Build();
        var services = new ServiceCollection();
        // The production log and trace pipeline: redacting JSON logs and the redacting span processor (T025).
        services.AddLogging();
        services.AddSingleton(new LogSink(_workerLogs));
        services.AddSingleton<ILoggerProvider, RedactingJsonLoggerProvider>();
        services.AddRrf5Telemetry(configuration, "rrf5-fiscal-worker-test");
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(_spans));
        services.AddMetrics();
        services.AddRrf5Foundation(configuration, "Worker");
        services.AddSingleton<ISefazTrust>(new TestSefazTrust());
        services.AddSingleton<ISefazEndpointCatalog>(new TestEndpointCatalog(_sefaz));
        services.AddSingleton<INfceSchemaProvider>(new TestSchemaProvider());
        services.AddSingleton(new EmissionOptions
        {
            Sefaz = new SefazClientOptions { ResponseTimeout = TimeSpan.FromSeconds(2) },
            ConsultBackoff = [TimeSpan.Zero],
        });
        return services.BuildServiceProvider();
    }

    private Dictionary<string, object?> Sale() => new()
    {
        ["externalId"] = $"pedido-{Guid.NewGuid():N}",
        ["nature"] = _nature,
        ["operation"] = "VENDA",
        ["items"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["productCode"] = "DOG-1", ["description"] = "Lanche sintetico", ["unit"] = "UN", ["gtin"] = "SEM GTIN",
                ["ncm"] = "21069090", ["origin"] = "0", ["cfop"] = "5101", ["csosn"] = "102", ["quantity"] = "2", ["unitPrice"] = "12.50",
            },
        },
        ["payments"] = new object[] { new { method = "CASH", amount = "30.00" } },
        ["change"] = "5.00",
        ["consumer"] = new { cpf = "12345678909" },
    };

    private static string NfePart(string envelope) => envelope[envelope.IndexOf("<NFe", StringComparison.Ordinal)..envelope.IndexOf("</NFe>", StringComparison.Ordinal)];

    private async Task<(string, string?, string, string, string?, string, string)> StoredAsync(Guid id)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            """
            select d.sent_xml_object_key, d.sent_xml_object_version, d.signed_xml_sha256, d.proc_xml_object_key, d.proc_xml_object_version, d.proc_xml_sha256, r.state
            from fiscal_document d join number_reservation r on r.document_id = d.id where d.id = $1
            """, owner);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6));
    }

    private async Task<string[]> ExchangesAsync(Guid id)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select operation || ':' || outcome from sefaz_exchange where document_id = $1 order by started_at", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return [.. result];
    }

    private async Task<long> NextNumberAsync()
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select next_number from fiscal_series where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = _series });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpMethod method, string path, string token, object? body = null) =>
        ApiClient.SendAsync(_client, method, path, token, body, method == HttpMethod.Get ? null : ApiClient.NewKey());

    private async Task Expect(HttpStatusCode status, HttpMethod method, string path, string token, object? body = null)
    {
        var (response, json) = await Send(method, path, token, body);
        Assert.True(response.StatusCode == status, $"{method} {path} returned {(int)response.StatusCode}: {json}");
    }

    private sealed class TestSefazTrust : ISefazTrust
    {
        public Task<X509ChainPolicy> PolicyForAsync(Authorizer authorizer, CancellationToken cancellationToken)
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(TestCertificates.RootCertificate());
            return Task.FromResult(policy);
        }
    }

    // SOAP services go to the test server; the QR Code and urlChave come from the real catalog.
    private sealed class TestEndpointCatalog(SefazTestServer server) : ISefazEndpointCatalog
    {
        private static readonly SefazEndpoints Real = SefazEndpoints.LoadEmbedded();

        public SefazEndpoint? Resolve(string authorizer, string model, string environment, string service) =>
            service is SefazEndpoints.Authorization or SefazEndpoints.ProtocolQuery
                ? server.Endpoint(service)
                : Real.Resolve(authorizer, model, environment, service);
    }

    private sealed class TestSchemaProvider : INfceSchemaProvider
    {
        private static readonly string ZipPath = Environment.GetEnvironmentVariable("RRF5_XSD_010G_ZIP")
            ?? @"C:\Users\rob_b\rrf5-private\sefaz\schemas\PL_010g_v1.03.zip";

        private static readonly Lazy<NfceSchema?> Schema = new(() => File.Exists(ZipPath) ? NfceSchema.FromPackage(File.ReadAllBytes(ZipPath)) : null);

        public Task<IReadOnlyList<string>> ValidateAsync(SchemaPackage package, string xml, CancellationToken cancellationToken) =>
            Task.FromResult(Schema.Value?.Validate(xml) ?? (IReadOnlyList<string>)[]);
    }
}
