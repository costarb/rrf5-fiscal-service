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
            "consult --xml <signed NFe> (--pfx <a1.pfx> [--out <nfeProc>] | --mock found|not-found)"
        }
    });
    return;
}

if (string.Equals(args[0], "authorize", StringComparison.Ordinal) || string.Equals(args[0], "consult", StringComparison.Ordinal))
{
    await RunSefazDocumentCommandAsync(args[0], ParseOptions(args[1..]));
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
    Fail("UNKNOWN_COMMAND", "Supported commands: validate, status, sign, verify, generate, check-key, authorize, consult.");
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
            Fail("SEFAZ_TRANSPORT_FAILED", $"{exception.GetType().Name}: {exception.Message} {exception.InnerException?.Message}".Trim());
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
                    notAfter = certificate.NotAfter.ToString("O")
                },
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
