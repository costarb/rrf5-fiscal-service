using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Schema;

const string ExpectedPackageSha256 = "d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1";

if (args.Length == 0)
{
    WriteResult(new
    {
        candidate = "dotnet",
        runtime = Environment.Version.ToString(),
        scope = "fiscal-discovery-poc",
        productionReady = false,
        commands = new[]
        {
            "validate --xml <path> --xsd <nfe_v4.00.xsd> --package <official.zip>",
            "status --pfx <a1.pfx outside the repository> (password from RRF5_A1_PASSWORD)",
            "sign --pfx <a1.pfx outside the repository> [--out <signed.xml outside the repository>]",
            "verify --xml <authorized nfeProc outside the repository> --cert-sha256 <expected A1 fingerprint>",
            "generate --case <fixture.json> --profile <profile.json> --issuer <issuer.json> --nnf <n> --cnf <8 digits> --dhemi <ISO-8601> [--out <path>]",
            "check-key --key <44-digit access key>",
            "authorize --xml <signed NFe> (--pfx <a1.pfx> --out <nfeProc outside the repository> | --mock authorized|schema-rejected|timeout) [--idlote <n>]",
            "consult --xml <signed NFe> (--pfx <a1.pfx> [--out <nfeProc>] | --mock found|not-found)",
            "danfe --xml <authorized nfeProc outside the repository> --qr-url <UF QR consultation URL> --out <html outside the repository> [--paper-mm 80] [--qr-mm 30] [--layout side|center] [--tz America/Sao_Paulo]",
            "danfe-selftest --case <fixture.json> --profile <profile.json> --issuer <synthetic issuer.json> [--out <html>]"
        }
    });
    return;
}

if (string.Equals(args[0], "authorize", StringComparison.Ordinal) || string.Equals(args[0], "consult", StringComparison.Ordinal))
{
    await RunSefazDocumentCommandAsync(args[0], ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "bench", StringComparison.Ordinal))
{
    RunBench(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "danfe", StringComparison.Ordinal))
{
    RunDanfe(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "danfe-selftest", StringComparison.Ordinal))
{
    RunDanfeSelfTest(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "generate", StringComparison.Ordinal))
{
    RunGenerate(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "check-key", StringComparison.Ordinal))
{
    var keyOptions = ParseOptions(args[1..]);
    if (!keyOptions.TryGetValue("--key", out var key) || key.Length != 44 || !key.All(char.IsAsciiDigit))
    {
        Fail("INVALID_KEY", "--key must be 44 digits.");
    }
    var expectedDv = NfceGenerator.AccessKeyCheckDigit(key![..43]);
    WriteResult(new { candidate = "dotnet", command = "check-key", checkDigitValid = expectedDv == key[43] - '0' });
    return;
}

if (string.Equals(args[0], "verify", StringComparison.Ordinal))
{
    RunVerify(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "status", StringComparison.Ordinal))
{
    await RunStatusAsync(ParseOptions(args[1..]));
    return;
}

if (string.Equals(args[0], "sign", StringComparison.Ordinal))
{
    RunSign(ParseOptions(args[1..]));
    return;
}

if (!string.Equals(args[0], "validate", StringComparison.Ordinal))
{
    Fail("UNKNOWN_COMMAND", "Supported commands: validate, status, sign, verify, generate, check-key, authorize, consult, danfe, danfe-selftest.");
}

var options = ParseOptions(args[1..]);
var xmlPath = RequiredFile(options, "--xml");
var xsdPath = RequiredFile(options, "--xsd");
var packagePath = RequiredFile(options, "--package");

var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
if (!string.Equals(packageHash, ExpectedPackageSha256, StringComparison.Ordinal))
{
    Fail("XSD_PACKAGE_HASH_MISMATCH", $"Expected {ExpectedPackageSha256}, got {packageHash}.");
}

var validationMessages = new List<object>();
var schemaDirectory = Path.GetDirectoryName(xsdPath)
    ?? throw new InvalidOperationException("The XSD path has no parent directory.");
var schemas = new XmlSchemaSet
{
    XmlResolver = new LocalOnlyXmlResolver(schemaDirectory)
};
schemas.ValidationEventHandler += (_, eventArgs) => validationMessages.Add(new
{
    severity = eventArgs.Severity.ToString().ToUpperInvariant(),
    message = eventArgs.Message
});
schemas.Add("http://www.portalfiscal.inf.br/nfe", xsdPath);
schemas.Compile();

var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null,
    ValidationType = ValidationType.Schema,
    Schemas = schemas
};
settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
settings.ValidationEventHandler += (_, eventArgs) => validationMessages.Add(new
{
    severity = eventArgs.Severity.ToString().ToUpperInvariant(),
    message = eventArgs.Message
});

using (var reader = XmlReader.Create(xmlPath, settings))
{
    while (reader.Read()) { }
}

WriteResult(new
{
    candidate = "dotnet",
    valid = validationMessages.Count == 0,
    schema = Path.GetFileName(xsdPath),
    packageSha256 = packageHash,
    messages = validationMessages
});

if (validationMessages.Count > 0)
{
    Environment.ExitCode = 2;
}

return;

// Read-only NFeStatusServico4 probe against SEFAZ-SP homologation. It proves A1
// loading, mutual TLS and SOAP 1.2 exchange without any fiscal document content.
static async Task RunStatusAsync(IReadOnlyDictionary<string, string> options)
{
    const string Endpoint = "https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeStatusServico4.asmx";
    const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    using (var certificate = LoadA1(options))
    {
        var envelope =
            "<soap12:Envelope xmlns:soap12=\"http://www.w3.org/2003/05/soap-envelope\"><soap12:Body>" +
            "<nfeDadosMsg xmlns=\"http://www.portalfiscal.inf.br/nfe/wsdl/NFeStatusServico4\">" +
            $"<consStatServ versao=\"4.00\" xmlns=\"{NfeNamespace}\"><tpAmb>2</tpAmb><cUF>35</cUF><xServ>STATUS</xServ></consStatServ>" +
            "</nfeDadosMsg></soap12:Body></soap12:Envelope>";

        SocketsHttpHandler handler;
        try
        {
            handler = SefazClient.CreateHandler(certificate);
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            Fail("TRUST_ROOT_REFUSED", exception.Message);
            return;
        }
        using var disposableHandler = handler;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(Endpoint, content);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var causes = new List<string>();
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                causes.Add($"{current.GetType().Name}: {current.Message}");
            }
            Fail("SEFAZ_TRANSPORT_FAILED",
                $"after {stopwatch.ElapsedMilliseconds} ms; pfxIntermediates={SefazClient.ClientIntermediates.Count}; " +
                $"trustRoot={(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SefazClient.TrustRootVariable)) ? "os-store" : "pinned")}; " +
                string.Join(" <- ", causes));
            return;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();

            var document = new XmlDocument { XmlResolver = null };
            try
            {
                using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document.Load(reader);
            }
            catch (XmlException)
            {
                Fail("SEFAZ_RESPONSE_NOT_XML", $"HTTP {(int)response.StatusCode} returned a non-XML body.");
            }

            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("nfe", NfeNamespace);
            var result = document.SelectSingleNode("//nfe:retConsStatServ", namespaces);
            string? Field(string name) => result?.SelectSingleNode($"nfe:{name}", namespaces)?.InnerText;

            WriteResult(new
            {
                candidate = "dotnet",
                command = "status",
                environment = "HOMOLOGATION",
                endpoint = Endpoint,
                httpStatus = (int)response.StatusCode,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                certificate = new
                {
                    sha256Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
                    notAfter = certificate.NotAfter.ToString("O"),
                    pfxIntermediates = SefazClient.ClientIntermediates.Count
                },
                trustRoot = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SefazClient.TrustRootVariable)) ? "os-store" : "pinned",
                sefaz = result is null ? null : new
                {
                    tpAmb = Field("tpAmb"),
                    verAplic = Field("verAplic"),
                    cStat = Field("cStat"),
                    xMotivo = Field("xMotivo"),
                    cUF = Field("cUF"),
                    dhRecbto = Field("dhRecbto"),
                    tMed = Field("tMed")
                }
            });

            if (result is null)
            {
                Environment.ExitCode = 2;
            }
        }
    }
}

// T017: enveloped XMLDSig over a synthetic, non-fiscal infNFe using the A1, then
// verification and a mutation-negative check. No document is transmitted.
static void RunSign(IReadOnlyDictionary<string, string> options)
{
    const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    using var certificate = LoadA1(options);
    using var privateKey = certificate.GetRSAPrivateKey();
    if (privateKey is null)
    {
        Fail("A1_NOT_RSA", "The A1 private key is not RSA.");
        return;
    }

    // Without --xml: synthetic non-fiscal payload. With --xml: an unsigned NFe
    // produced by `generate`; the Signature is appended to NFe after infNFeSupl.
    var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
    var documentMode = options.TryGetValue("--xml", out var inputXml);
    if (documentMode)
    {
        if (!options.ContainsKey("--out"))
        {
            Fail("OUT_REQUIRED", "Signing a document requires --out outside the repository.");
        }
        document.Load(RequiredFile(options, "--xml"));
    }
    else
    {
        document.LoadXml(
            $"<NFe xmlns=\"{NfeNamespace}\"><infNFe Id=\"NFe00000000000000000000000000000000000000000000\" versao=\"4.00\">" +
            "<poc>SYNTHETIC NON-FISCAL XMLDSIG TEST - NO TAX CONTENT</poc><vTest>10.00</vTest>" +
            "</infNFe></NFe>");
    }
    var signNamespaces = new XmlNamespaceManager(document.NameTable);
    signNamespaces.AddNamespace("nfe", NfeNamespace);
    signNamespaces.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
    var infNfeNodes = document.SelectNodes("/nfe:NFe/nfe:infNFe", signNamespaces)!;
    if (infNfeNodes.Count != 1 || document.SelectNodes("//ds:Signature", signNamespaces)!.Count != 0)
    {
        Fail("UNEXPECTED_STRUCTURE", "Expected one NFe/infNFe and no existing Signature.");
    }
    var ReferenceId = ((XmlElement)infNfeNodes[0]!).GetAttribute("Id");
    var mutationPath = documentMode ? "//nfe:infNFe/nfe:total/nfe:ICMSTot/nfe:vNF" : "//nfe:vTest";

    var signedXml = new SignedXml(document) { SigningKey = privateKey };
    signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
    signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
    var reference = new Reference($"#{ReferenceId}") { DigestMethod = SignedXml.XmlDsigSHA1Url };
    reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
    reference.AddTransform(new XmlDsigC14NTransform());
    signedXml.AddReference(reference);
    var keyInfo = new KeyInfo();
    keyInfo.AddClause(new KeyInfoX509Data(certificate));
    signedXml.KeyInfo = keyInfo;
    signedXml.ComputeSignature();
    document.DocumentElement!.AppendChild(document.ImportNode(signedXml.GetXml(), true));
    var signedText = document.OuterXml;

    var verified = VerifyNfeSignature(signedText, ReferenceId, certificate);

    var mutated = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
    mutated.LoadXml(signedText);
    var namespaces = new XmlNamespaceManager(mutated.NameTable);
    namespaces.AddNamespace("nfe", NfeNamespace);
    var mutationTarget = mutated.SelectSingleNode(mutationPath, namespaces)!;
    mutationTarget.InnerText = mutationTarget.InnerText == "10.01" ? "10.02" : "10.01";
    var mutationRejected = !VerifyNfeSignature(mutated.OuterXml, ReferenceId, certificate);

    if (options.TryGetValue("--out", out var outPath))
    {
        var fullOut = Path.GetFullPath(outPath);
        if (fullOut.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase))
        {
            Fail("OUTPUT_INSIDE_REPOSITORY", "Signed XML embeds the A1 public certificate; write it outside the repository.");
        }
        File.WriteAllText(fullOut, signedText, new UTF8Encoding(false));
    }

    WriteResult(new
    {
        candidate = "dotnet",
        command = "sign",
        scope = documentMode ? "generated-nfe-document" : "synthetic-non-fiscal-xml",
        signatureMethod = SignedXml.XmlDsigRSASHA1Url,
        digestMethod = SignedXml.XmlDsigSHA1Url,
        canonicalization = SignedXml.XmlDsigC14NTransformUrl,
        reference = documentMode ? "#NFe<access key>" : $"#{ReferenceId}",
        certificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
        signatureVerified = verified,
        mutationRejected
    });

    if (!verified || !mutationRejected)
    {
        Environment.ExitCode = 2;
    }
}

// T018/T019: authorize (NFeAutorizacao4, indSinc=1) or consult
// (NFeConsultaProtocolo4) one signed NFC-e in SEFAZ-SP homologation, or run the
// same path against a mock transport. Output is sanitized: no access key, issuer
// data or raw body. Exit codes: 0 authorized, 2 not authorized / not found,
// 3 RESULT_UNKNOWN.
static async Task RunSefazDocumentCommandAsync(string command, IReadOnlyDictionary<string, string> options)
{
    SefazClient.SignedDocument document;
    try
    {
        document = SefazClient.LoadSigned(RequiredFile(options, "--xml"));
    }
    catch (Exception exception) when (exception is InvalidDataException or XmlException)
    {
        Fail("DOCUMENT_REFUSED", exception.Message);
        return;
    }

    var mock = options.TryGetValue("--mock", out var scenario);
    string? outPath = null;
    if (options.TryGetValue("--out", out var requestedOut))
    {
        outPath = Path.GetFullPath(requestedOut);
        if (outPath.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase))
        {
            Fail("OUTPUT_INSIDE_REPOSITORY", "Fiscal documents and SEFAZ answers stay outside the repository.");
        }
    }
    if (!mock && command == "authorize" && outPath is null)
    {
        Fail("OUT_REQUIRED", "A real authorization must persist the nfeProc: pass --out outside the repository.");
    }

    X509Certificate2? certificate = null;
    SefazClient.Transport transport;
    if (mock)
    {
        transport = SefazClient.MockTransport(scenario!, document);
    }
    else
    {
        certificate = LoadA1(options);
        try
        {
            // Fail before transmission if the configured trust root is wrong.
            using (SefazClient.CreateHandler(certificate)) { }
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            Fail("TRUST_ROOT_REFUSED", exception.Message);
        }
        transport = SefazClient.HttpTransport(certificate);
    }

    using (certificate)
    {
        XmlDocument response;
        SefazClient.Exchange exchange;
        long elapsedMs;
        try
        {
            if (command == "authorize")
            {
                var idLote = options.TryGetValue("--idlote", out var lote) ? lote : DateTime.UtcNow.ToString("yyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
                if (idLote.Length is 0 or > 15 || !idLote.All(char.IsAsciiDigit))
                {
                    Fail("INVALID_ARGUMENTS", "--idlote must have 1 to 15 digits.");
                }
                (response, exchange, elapsedMs) = await SefazClient.AuthorizeAsync(transport, document, idLote);
            }
            else
            {
                (response, exchange, elapsedMs) = await SefazClient.ConsultAsync(transport, document.AccessKey);
            }
        }
        catch (SefazClient.UncertainResultException exception)
        {
            WriteResult(new
            {
                candidate = "dotnet",
                command,
                mock = mock ? scenario : null,
                outcome = "RESULT_UNKNOWN",
                reason = exception.Message,
                next = "Run `consult` with the same signed document. Do not regenerate, renumber or resend before the consultation answers."
            });
            Environment.ExitCode = 3;
            return;
        }

        var ns = SefazClient.Namespaces(response);
        var ret = response.SelectSingleNode(command == "authorize" ? "//nfe:retEnviNFe" : "//nfe:retConsSitNFe", ns);
        var prot = ret?.SelectSingleNode("nfe:protNFe/nfe:infProt", ns);
        string? Field(XmlNode? node, string name) => node?.SelectSingleNode($"nfe:{name}", ns)?.InnerText;

        var protocolKeyMatches = prot is not null && Field(prot, "chNFe") == document.AccessKey;
        var protocolDigestMatches = prot is not null && Field(prot, "digVal") == document.DigestValue;
        var authorized = Field(prot, "cStat") == "100" && protocolKeyMatches && protocolDigestMatches;
        // NOT_FOUND (217 on consultation) is the only answer that, together with an
        // explicit approved policy, may later allow a resend (pocs/shared/uncertain-result).
        var outcome = ret is null ? "RESULT_UNKNOWN"
            : authorized ? "AUTHORIZED"
            : command == "consult" && Field(ret, "cStat") == "217" ? "NOT_FOUND"
            : "NOT_AUTHORIZED";

        if (!mock && outPath is not null)
        {
            File.WriteAllText(outPath + ".ret.xml", exchange.Body, new UTF8Encoding(false));
        }
        if (authorized && outPath is not null)
        {
            File.WriteAllText(outPath, SefazClient.BuildNfeProc(document, prot!.ParentNode!), new UTF8Encoding(false));
        }

        WriteResult(new
        {
            candidate = "dotnet",
            command,
            mock = mock ? scenario : null,
            environment = "HOMOLOGATION",
            trace = new { httpStatus = exchange.HttpStatus, elapsedMs, responseSha256 = SefazClient.Sha256(exchange.Body) },
            sefaz = ret is null ? null : new
            {
                tpAmb = Field(ret, "tpAmb"),
                verAplic = Field(ret, "verAplic"),
                cStat = Field(ret, "cStat"),
                xMotivo = Field(ret, "xMotivo")
            },
            protocol = prot is null ? null : new
            {
                cStat = Field(prot, "cStat"),
                xMotivo = Field(prot, "xMotivo"),
                nProt = Field(prot, "nProt"),
                keyMatches = protocolKeyMatches,
                digestMatches = protocolDigestMatches
            },
            outcome,
            nfeProcWritten = authorized && outPath is not null
        });

        Environment.ExitCode = outcome switch { "AUTHORIZED" => 0, "NOT_AUTHORIZED" or "NOT_FOUND" => 2, _ => 3 };
    }
}

// Performance comparison: generate -> sign -> verify per iteration with a fresh
// nNF (so the access key and digest change), after a warm-up. Synthetic inputs and
// a throwaway key only. Reports throughput, latency percentiles and allocation.
static void RunBench(IReadOnlyDictionary<string, string> options)
{
    JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllBytes(RequiredFile(options, name))).RootElement;
    var fixture = Load("--case");
    var profile = Load("--profile");
    var issuer = Load("--issuer");
    var iterations = int.Parse(options.GetValueOrDefault("--iterations", "2000"), System.Globalization.CultureInfo.InvariantCulture);
    var warmup = int.Parse(options.GetValueOrDefault("--warmup", "200"), System.Globalization.CultureInfo.InvariantCulture);
    using var certificate = LoadA1(options);
    using var key = certificate.GetRSAPrivateKey()!;
    var dhEmi = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(-3));

    bool Once(int n)
    {
        var xml = NfceGenerator.Generate(fixture, profile, issuer, n, "12345678", dhEmi);
        var signed = BenchSigner.Sign(xml.Xml, certificate, key);
        return VerifyNfeSignature(signed, "NFe" + xml.AccessKey, certificate);
    }

    for (var i = 1; i <= warmup; i++)
    {
        if (!Once(i)) Fail("BENCH_VERIFY_FAILED", "Warm-up iteration failed verification.");
    }

    var samples = new double[iterations];
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var total = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Once(warmup + 1 + i)) Fail("BENCH_VERIFY_FAILED", "Iteration failed verification.");
        samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    total.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Array.Sort(samples);
    double Percentile(double p) => samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(p * samples.Length) - 1)];

    WriteResult(new
    {
        candidate = "dotnet",
        command = "bench",
        runtime = Environment.Version.ToString(),
        iterations,
        warmup,
        opsPerSecond = Math.Round(iterations / total.Elapsed.TotalSeconds, 1),
        p50Ms = Math.Round(Percentile(0.50), 3),
        p95Ms = Math.Round(Percentile(0.95), 3),
        p99Ms = Math.Round(Percentile(0.99), 3),
        allocatedKbPerOp = Math.Round(allocated / 1024.0 / iterations, 1),
        peakWorkingSetMb = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1)
    });
}

// T020: renders the DANFE NFC-e (HTML) of an authorized nfeProc. A real issuer's
// document and DANFE carry taxpayer data: both must live outside the repository,
// and the output does not print the access key.
static void RunDanfe(IReadOnlyDictionary<string, string> options)
{
    var xmlPath = RequiredFile(options, "--xml");
    if (!options.TryGetValue("--qr-url", out var qrUrl) || !qrUrl.StartsWith("https://", StringComparison.Ordinal))
    {
        Fail("QR_URL_REQUIRED", "--qr-url must be the UF's https QR consultation URL for the document's environment (Portal NFC-e, 'URL por UF').");
    }
    if (!options.TryGetValue("--out", out var outPath))
    {
        Fail("OUT_REQUIRED", "--out <html> is required; the DANFE is not printed to the console.");
    }

    var document = LoadXmlSafely(xmlPath);
    var synthetic = document.GetElementsByTagName("CNPJ", NfceGenerator.NfeNamespace) is { Count: > 0 } c && c[0]!.InnerText == "00000000000000";
    var fullOut = Path.GetFullPath(outPath!);
    if (!synthetic && (xmlPath.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase) || fullOut.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase)))
    {
        Fail("INSIDE_REPOSITORY", "A real issuer's nfeProc and DANFE carry taxpayer data; keep both outside the repository.");
    }

    Danfe.Result result;
    try
    {
        result = Danfe.Render(document, DanfeOptions(options, qrUrl!));
    }
    catch (Exception exception) when (exception is InvalidDataException or FormatException or TimeZoneNotFoundException)
    {
        Fail("DANFE_REFUSED", exception.Message);
        return;
    }
    File.WriteAllText(fullOut, result.Html, new UTF8Encoding(false));
    WriteResult(new
    {
        candidate = "dotnet",
        command = "danfe",
        syntheticIssuer = synthetic,
        accessKey = synthetic ? result.AccessKey : null,
        homologation = result.Homologation,
        divisions = result.Divisions,
        qr = new { version = result.QrVersion, modules = result.QrModules, moduleMm = result.QrModuleMm, marginMmPerSide = result.QrQuietMm, isoQuietZone = result.QrIsoQuietZone, totalMm = result.QrTotalMm, ecc = "M" },
        htmlSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Html))).ToLowerInvariant()
    });
}

static Danfe.Options DanfeOptions(IReadOnlyDictionary<string, string> options, string qrUrl)
{
    double Number(string name, string fallback) => double.Parse(options.GetValueOrDefault(name, fallback), System.Globalization.CultureInfo.InvariantCulture);
    var layout = options.GetValueOrDefault("--layout", "side");
    if (layout is not ("side" or "center"))
    {
        Fail("INVALID_ARGUMENTS", "--layout must be side or center (§3.1.5).");
    }
    return new Danfe.Options(Number("--paper-mm", "80"), Number("--qr-mm", "30"), layout == "center",
        TimeZoneInfo.FindSystemTimeZoneById(options.GetValueOrDefault("--tz", "America/Sao_Paulo")), qrUrl);
}

static XmlDocument LoadXmlSafely(string path)
{
    var document = new XmlDocument { XmlResolver = null };
    using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    document.Load(reader);
    return document;
}

// T020 self-test, synthetic only: manual QR vectors (v2 online/offline hash),
// v3 online/offline layout and RSA-SHA1 signature with a throwaway key, and a
// DANFE rendered from a synthetic nfeProc plus fail-closed negative cases.
static void RunDanfeSelfTest(IReadOnlyDictionary<string, string> options)
{
    JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllBytes(RequiredFile(options, name))).RootElement;
    var issuer = Load("--issuer");
    if (issuer.GetProperty("cnpj").GetString() != "00000000000000")
    {
        Fail("SYNTHETIC_ONLY", "danfe-selftest accepts only the synthetic issuer.");
    }
    var qrUrl = issuer.GetProperty("qrCodeUrl").GetString()!;
    var checks = new List<object>();
    var failed = 0;
    void Check(string name, bool ok, string? detail = null)
    {
        checks.Add(new { name, ok, detail });
        if (!ok) failed++;
    }
    void Refused(string name, Action action)
    {
        try { action(); Check(name, false, "accepted"); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException) { Check(name, true, exception.Message); }
    }

    // Manual §4.3.6.1 / §4.3.6.2 vectors (hypothetical CSC printed in the manual).
    const string ManualCsc = "SEU-CODIGO-CSC-CONTRIBUINTE-36-CARACTERES";
    Check("v2 online hash = manual §4.3.6.1",
        NfceQrCode.V2OnlineHash("28170800156225000131650110000151341562040824", "1", "000001", ManualCsc) == "DC6AE2C2B9A992BE59679AC365E29922DE6B7511");
    Check("v2 offline digVal hex = manual §4.3.6.2",
        NfceQrCode.V2OfflineDigestHex("yzGYhUx1/XYYzksWB+fPR3Qc50c=") == "797a4759685578312f5859597a6b7357422b6650523351633530633d");
    Check("v2 offline hash = manual §4.3.6.2",
        NfceQrCode.V2OfflineHash("28170800156225000131650110000151349562040824", "1", "02", "60.90", "yzGYhUx1/XYYzksWB+fPR3Qc50c=", "1", ManualCsc) == "4615A93BB0D7C4E780F8D30EE77EDD5BA55C7D66");

    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=RRF5 POC THROWAWAY - NOT AN A1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

    // Synthetic document -> throwaway signature -> synthetic protocol -> DANFE.
    var generated = NfceGenerator.Generate(Load("--case"), Load("--profile"), issuer, 1, "12345678", new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(-3)));
    var key = generated.AccessKey;
    Check("v3 online qrCode in generated XML (§4.4.1)", generated.Xml.Contains($"<qrCode>{NfceQrCode.V3Online(qrUrl, key, "2")}</qrCode>", StringComparison.Ordinal));

    var offlineText = NfceQrCode.V3OfflineSignedText(key, "2", 6, "10.00", null, null);
    Check("v3 offline: unidentified recipient keeps only separators (§4.4.2)", offlineText == $"{key}|3|2|06|10.00||");
    var offlineUrl = NfceQrCode.V3Offline(qrUrl, offlineText, rsa);
    var signature = Convert.FromBase64String(offlineUrl[(offlineUrl.LastIndexOf('|') + 1)..]);
    Check("v3 offline: RSA-SHA1 signature verifies with the signing certificate, no CSC (§4.4.2)",
        certificate.GetRSAPublicKey()!.VerifyData(Encoding.UTF8.GetBytes(offlineText), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
    Check("v3 offline: parser recognizes 8-parameter layout", NfceQrCode.Parse(offlineUrl, qrUrl) is { Version: 3, Offline: true });
    Check("v3 offline: CPF recipient", NfceQrCode.V3OfflineSignedText(key, "2", 6, "10.00", "2", "00000000191") == $"{key}|3|2|06|10.00|2|00000000191");
    Refused("v3 offline: vNF with comma refused", () => NfceQrCode.V3OfflineSignedText(key, "2", 6, "10,00", null, null));

    var signed = BenchSigner.Sign(generated.Xml, certificate, rsa);
    var nfe = signed[signed.IndexOf("<NFe", StringComparison.Ordinal)..];
    var digest = System.Text.RegularExpressions.Regex.Match(nfe, "<DigestValue>([^<]+)</DigestValue>").Groups[1].Value;
    string Proc(string body, string cStat = "100", string chNFe = "") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"{NfceGenerator.NfeNamespace}\" versao=\"4.00\">{body}" +
        $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>SELFTEST</verAplic><chNFe>{(chNFe == "" ? key : chNFe)}</chNFe>" +
        $"<dhRecbto>2026-11-06T15:00:01Z</dhRecbto><nProt>135000000000000</nProt><digVal>{digest}</digVal><cStat>{cStat}</cStat><xMotivo>SELFTEST</xMotivo></infProt></protNFe></nfeProc>";
    XmlDocument Doc(string xml) { var d = new XmlDocument { XmlResolver = null }; d.LoadXml(xml); return d; }
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    Danfe.Options Opts(double paper = 80, double qr = 30, bool center = false, string? url = null) => new(paper, qr, center, zone, url ?? qrUrl);

    var result = Danfe.Render(Doc(Proc(nfe)), Opts());
    var html = result.Html;
    foreach (var (label, expected) in new[]
    {
        ("I: issuer CNPJ mask", "CNPJ: 00.000.000/0000-00"),
        ("I: fixed title", "Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica"),
        ("II: item code", "SYNTHETIC-PRODUCT-001"),
        ("III: total with comma decimal", "<td class=\"r\">10,00</td>"),
        ("III: payment line", "Dinheiro"),
        ("IV: consultation text", "Consulte pela Chave de Acesso em"),
        ("IV: key in 11 blocks", string.Join(' ', Enumerable.Range(0, 11).Select(i => key.Substring(i * 4, 4)))),
        ("VI: unidentified consumer", "CONSUMIDOR NÃO IDENTIFICADO"),
        ("VII: number/series/emission local time", "NFC-e nº 000000001 Série 001 06/11/2026 12:00:00"),
        ("VII: authorization UTC converted to local", "06/11/2026 12:00:01"),
        ("VIII: homologation notice", "EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL")
    })
    {
        Check(label, html.Contains(expected, StringComparison.Ordinal));
    }
    Check("III: 'Valor a Pagar' omitted without additions/discount (§3.1.3)", !html.Contains("Valor a Pagar", StringComparison.Ordinal));
    Check("V: QR module count matches version (17+4v)", result.QrModules == 17 + 4 * result.QrVersion, $"version {result.QrVersion}, {result.QrModules} modules");
    Check("V: 30 mm QR = 27 mm content + 10% margin (§3.4)",
        Math.Abs(result.QrModules * result.QrModuleMm - 27) < 0.05 && Math.Abs(result.QrQuietMm - 1.5) < 0.01,
        $"{result.QrModules * result.QrModuleMm:0.00} mm content, {result.QrQuietMm} mm margin/side, ISO 4-module quiet zone met: {result.QrIsoQuietZone}");
    var minimum = Danfe.Render(Doc(Proc(nfe)), Opts(qr: 25));
    Check("V: 25 mm minimum = 22 mm content + 3 mm margin (§3.4)",
        Math.Abs(minimum.QrModules * minimum.QrModuleMm - 22) < 0.05 && Math.Abs(minimum.QrQuietMm - 1.5) < 0.01,
        $"module {minimum.QrModuleMm} mm, ISO 4-module quiet zone met: {minimum.QrIsoQuietZone}");
    Check("V: centered layout renders (§3.1.5)", Danfe.Render(Doc(Proc(nfe)), Opts(center: true)).Html.Contains("qr centered", StringComparison.Ordinal));
    Check("money format 1.234.567,89", Danfe.Money(1234567.89m) == "1.234.567,89");
    Check("quantity format 1,5 / 2", Danfe.Qty("1.5000") == "1,5" && Danfe.Qty("2.0000") == "2");

    Refused("refuse: no protocol (unsigned/unauthorized)", () => Danfe.Render(Doc($"<nfeProc xmlns=\"{NfceGenerator.NfeNamespace}\">{nfe}</nfeProc>"), Opts()));
    Refused("refuse: protocol not 100", () => Danfe.Render(Doc(Proc(nfe, cStat: "110")), Opts()));
    Refused("refuse: protocol for another key", () => Danfe.Render(Doc(Proc(nfe, chNFe: new string('9', 44))), Opts()));
    Refused("refuse: offline contingency tpEmis 9", () => Danfe.Render(Doc(Proc(nfe.Replace("<tpEmis>1</tpEmis>", "<tpEmis>9</tpEmis>", StringComparison.Ordinal))), Opts()));
    Refused("refuse: qrCode for another key", () => Danfe.Render(Doc(Proc(nfe.Replace($"?p={key}|", $"?p={new string('9', 44)}|", StringComparison.Ordinal))), Opts()));
    Refused("refuse: qrCode environment differs", () => Danfe.Render(Doc(Proc(nfe.Replace($"{key}|3|2<", $"{key}|3|1<", StringComparison.Ordinal))), Opts()));
    Refused("refuse: QR URL of another UF/environment", () => Danfe.Render(Doc(Proc(nfe)), Opts(url: "https://example.invalid/qrcode")));
    Refused("refuse: paper < 56 mm (§3.3)", () => Danfe.Render(Doc(Proc(nfe)), Opts(paper: 50)));
    Refused("refuse: QR < 25 mm (§3.4)", () => Danfe.Render(Doc(Proc(nfe)), Opts(qr: 24)));

    if (options.TryGetValue("--out", out var outPath))
    {
        File.WriteAllText(Path.GetFullPath(outPath), html, new UTF8Encoding(false));
    }
    WriteResult(new
    {
        candidate = "dotnet",
        command = "danfe-selftest",
        passed = checks.Count - failed,
        failed,
        qr = new { version = result.QrVersion, modules = result.QrModules, moduleMm = result.QrModuleMm, marginMmPerSide = result.QrQuietMm, isoQuietZone = result.QrIsoQuietZone },
        checks
    });
    Environment.ExitCode = failed == 0 ? 0 : 1;
}

// T016: deterministic NFC-e generation. A synthetic issuer (all-zero CNPJ) may
// write inside the repository and prints its access key; any other issuer is
// treated as taxpayer data: output must go outside the repository and the access
// key (which embeds the CNPJ) is not printed.
static void RunGenerate(IReadOnlyDictionary<string, string> options)
{
    JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllBytes(RequiredFile(options, name))).RootElement;

    var fixture = Load("--case");
    var profile = Load("--profile");
    var issuer = Load("--issuer");
    var synthetic = issuer.GetProperty("cnpj").GetString() == "00000000000000";

    if (!options.TryGetValue("--nnf", out var nnfText) || !int.TryParse(nnfText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nNF)
        || !options.TryGetValue("--cnf", out var cNF)
        || !options.TryGetValue("--dhemi", out var dhEmiText)
        || !DateTimeOffset.TryParseExact(dhEmiText, "yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dhEmi))
    {
        Fail("INVALID_ARGUMENTS", "--nnf <int>, --cnf <8 digits> and --dhemi <yyyy-MM-ddTHH:mm:ss+hh:mm> are required; nothing is taken from the clock or RNG.");
        return;
    }

    NfceGenerator.Result result;
    try
    {
        result = NfceGenerator.Generate(fixture, profile, issuer, nNF, cNF!, dhEmi);
    }
    catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException)
    {
        Fail("GENERATION_REFUSED", exception.Message);
        return;
    }

    if (options.TryGetValue("--out", out var outPath))
    {
        var fullOut = Path.GetFullPath(outPath);
        if (!synthetic && fullOut.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase))
        {
            Fail("OUTPUT_INSIDE_REPOSITORY", "A document for a real issuer carries taxpayer data; write it outside the repository.");
        }
        File.WriteAllText(fullOut, result.Xml, new UTF8Encoding(false));
    }
    else if (!synthetic)
    {
        Fail("OUT_REQUIRED", "A real issuer requires --out outside the repository; the XML is not printed.");
    }

    WriteResult(new
    {
        candidate = "dotnet",
        command = "generate",
        profile = profile.GetProperty("profileId").GetString(),
        syntheticIssuer = synthetic,
        accessKey = synthetic ? result.AccessKey : null,
        xmlSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Xml))).ToLowerInvariant(),
        bytes = Encoding.UTF8.GetByteCount(result.Xml)
    });
}

// T017 cross-check: verifies the XMLDSig of a document already authorized by
// SEFAZ (for example, issued by a third-party ERP with the same A1). Needs only
// the public certificate embedded in the XML; no private key or password. The
// output is limited to booleans, algorithm URIs, fingerprint and status fields:
// no access key, issuer identifier or item data is printed.
static void RunVerify(IReadOnlyDictionary<string, string> options)
{
    const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    var xmlPath = RequiredFile(options, "--xml");
    if (xmlPath.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase))
    {
        Fail("XML_INSIDE_REPOSITORY", "Authorized XML carries taxpayer data; keep it outside the repository.");
    }
    if (!options.TryGetValue("--cert-sha256", out var expectedFingerprint) || expectedFingerprint.Length != 64)
    {
        Fail("FINGERPRINT_REQUIRED", "--cert-sha256 must be the expected 64-hex A1 SHA-256 fingerprint.");
    }

    var text = File.ReadAllText(xmlPath, Encoding.UTF8);
    var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
    document.LoadXml(text);
    var namespaces = new XmlNamespaceManager(document.NameTable);
    namespaces.AddNamespace("nfe", NfeNamespace);
    namespaces.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

    var infNfe = document.SelectNodes("//nfe:infNFe", namespaces)!;
    var certificateNodes = document.SelectNodes("//ds:Signature/ds:KeyInfo/ds:X509Data/ds:X509Certificate", namespaces)!;
    if (infNfe.Count != 1 || certificateNodes.Count != 1)
    {
        Fail("UNEXPECTED_STRUCTURE", "Expected exactly one infNFe and one embedded X509Certificate.");
    }
    var referenceId = ((XmlElement)infNfe[0]!).GetAttribute("Id");

    using var embedded = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateNodes[0]!.InnerText.Trim()));
    var fingerprint = embedded.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
    var fingerprintMatches = string.Equals(fingerprint, expectedFingerprint!.ToLowerInvariant(), StringComparison.Ordinal);

    var verified = VerifyNfeSignature(text, referenceId, embedded);

    // Mutation-negative: one cent added to the document total must break the signature.
    var mutated = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
    mutated.LoadXml(text);
    var vNf = mutated.SelectSingleNode("//nfe:infNFe/nfe:total/nfe:ICMSTot/nfe:vNF", namespaces);
    if (vNf is null)
    {
        Fail("UNEXPECTED_STRUCTURE", "total/ICMSTot/vNF not found.");
    }
    vNf!.InnerText = (decimal.Parse(vNf.InnerText, System.Globalization.CultureInfo.InvariantCulture) + 0.01m)
        .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    var mutationRejected = !VerifyNfeSignature(mutated.OuterXml, referenceId, embedded);

    string? Value(string xpath) => document.SelectSingleNode(xpath, namespaces)?.InnerText;
    var digestValue = Value("//ds:Signature/ds:SignedInfo/ds:Reference/ds:DigestValue");
    var protocolDigest = Value("//nfe:protNFe/nfe:infProt/nfe:digVal");

    WriteResult(new
    {
        candidate = "dotnet",
        command = "verify",
        scope = "authorized-document-signature-check",
        fileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(xmlPath))).ToLowerInvariant(),
        canonicalization = document.SelectSingleNode("//ds:SignedInfo/ds:CanonicalizationMethod/@Algorithm", namespaces)?.Value,
        signatureMethod = document.SelectSingleNode("//ds:SignedInfo/ds:SignatureMethod/@Algorithm", namespaces)?.Value,
        digestMethod = document.SelectSingleNode("//ds:SignedInfo/ds:Reference/ds:DigestMethod/@Algorithm", namespaces)?.Value,
        certificateSha256Matches = fingerprintMatches,
        signatureVerified = verified,
        mutationRejected,
        protocolDigestMatches = digestValue is not null && digestValue == protocolDigest,
        tpAmb = Value("//nfe:protNFe/nfe:infProt/nfe:tpAmb"),
        cStat = Value("//nfe:protNFe/nfe:infProt/nfe:cStat"),
        verAplic = Value("//nfe:protNFe/nfe:infProt/nfe:verAplic")
    });

    if (!fingerprintMatches || !verified || !mutationRejected)
    {
        Environment.ExitCode = 2;
    }
}

// Accepts only one Signature with one Reference that points at the single infNFe
// carrying the expected Id, so a wrapped or re-targeted reference fails closed.
static bool VerifyNfeSignature(string xml, string expectedId, X509Certificate2 expectedCertificate)
{
    var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
    document.LoadXml(xml);
    var namespaces = new XmlNamespaceManager(document.NameTable);
    namespaces.AddNamespace("nfe", "http://www.portalfiscal.inf.br/nfe");
    namespaces.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

    var signatures = document.SelectNodes("//ds:Signature", namespaces)!;
    var targets = document.SelectNodes($"//*[@Id='{expectedId}']", namespaces)!;
    if (signatures.Count != 1 || targets.Count != 1 || targets[0]!.LocalName != "infNFe")
    {
        return false;
    }

    var signedXml = new SignedXml(document);
    signedXml.LoadXml((XmlElement)signatures[0]!);
    if (signedXml.SignedInfo!.References.Count != 1
        || signedXml.SignedInfo.References[0] is not Reference reference
        || reference.Uri != $"#{expectedId}")
    {
        return false;
    }

    var embedded = signedXml.KeyInfo.OfType<KeyInfoX509Data>()
        .SelectMany(data => data.Certificates!.Cast<X509Certificate2>())
        .SingleOrDefault();
    if (embedded is null || !embedded.RawDataMemory.Span.SequenceEqual(expectedCertificate.RawDataMemory.Span))
    {
        return false;
    }

    return signedXml.CheckSignature(embedded, verifySignatureOnly: true);
}

static X509Certificate2 LoadA1(IReadOnlyDictionary<string, string> options)
{
    const string PasswordVariable = "RRF5_A1_PASSWORD";

    var pfxPath = RequiredFile(options, "--pfx");
    if (pfxPath.StartsWith(RepositoryRoot(), StringComparison.OrdinalIgnoreCase))
    {
        Fail("A1_INSIDE_REPOSITORY", "The A1 file must live outside the repository working tree.");
    }

    var password = Environment.GetEnvironmentVariable(PasswordVariable);
    if (string.IsNullOrEmpty(password))
    {
        Fail("A1_PASSWORD_MISSING", $"Set {PasswordVariable}; the password is never accepted as an argument.");
    }

    X509Certificate2 certificate;
    try
    {
        certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
    }
    catch (CryptographicException)
    {
        Fail("A1_LOAD_FAILED", "The PKCS#12 file could not be opened with the supplied password.");
        throw;
    }

    // Keep CA certificates bundled in the PFX (not the leaf) to present the client
    // chain without network access.
    var bundle = X509CertificateLoader.LoadPkcs12CollectionFromFile(pfxPath, password);
    SefazClient.ClientIntermediates = [.. bundle.Where(c => c.Thumbprint != certificate.Thumbprint && c.Subject != c.Issuer)];

    var now = DateTime.Now;
    if (!certificate.HasPrivateKey)
    {
        Fail("A1_NO_PRIVATE_KEY", "The PKCS#12 file does not contain a private key.");
    }
    if (now < certificate.NotBefore || now > certificate.NotAfter)
    {
        Fail("A1_OUT_OF_VALIDITY", $"Certificate valid from {certificate.NotBefore:O} to {certificate.NotAfter:O}.");
    }
    return certificate;
}

// Nearest ancestor of the binary that contains .git. In a container image there
// is no working tree, so a sentinel that no real path starts with is returned.
static string RepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            return directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }
    return "\0no-repository\0";
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    if (values.Length % 2 != 0)
    {
        Fail("INVALID_ARGUMENTS", "Options must be provided as --name <value> pairs.");
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(values[index], values[index + 1]))
        {
            Fail("INVALID_ARGUMENTS", $"Invalid or duplicate option: {values[index]}.");
        }
    }
    return result;
}

static string RequiredFile(IReadOnlyDictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out var path) || !File.Exists(path))
    {
        Fail("FILE_NOT_FOUND", $"{name} must reference an existing file.");
    }
    return Path.GetFullPath(path!);
}

static void WriteResult(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

static void Fail(string code, string message)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { candidate = "dotnet", valid = false, error = code, message }));
    Environment.Exit(1);
}

sealed class LocalOnlyXmlResolver(string rootDirectory) : XmlResolver
{
    private readonly string _root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;

    public override ICredentials? Credentials { set { } }

    public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        if (!absoluteUri.IsFile)
        {
            throw new XmlException("Only local XSD imports are allowed.");
        }

        var localPath = absoluteUri.LocalPath;
        if (string.IsNullOrEmpty(localPath))
        {
            throw new XmlException("XSD import has no local path.");
        }

        var path = Path.GetFullPath(localPath);
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new XmlException("XSD import escaped the approved schema directory.");
        }

        return File.OpenRead(path);
    }
}
