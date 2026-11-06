import { createHash, X509Certificate } from "node:crypto";
import { existsSync } from "node:fs";
import { readdir, readFile, stat, writeFile } from "node:fs/promises";
import { request } from "node:https";
import { basename, dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { validateXML, type XMLFileInfo } from "xmllint-wasm";
import { algorithms, embeddedCertificate, loadA1, mutateTotal, sign, verify } from "./dsig.js";
import { accessKeyCheckDigit, generate, RefusedError } from "./generate.js";

const EXPECTED_PACKAGE_SHA256 =
  "d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1";

function fail(code: string, message: string): never {
  process.stderr.write(`${JSON.stringify({ candidate: "typescript", valid: false, error: code, message })}\n`);
  process.exit(1);
}

function parseOptions(values: string[]): Map<string, string> {
  if (values.length % 2 !== 0) fail("INVALID_ARGUMENTS", "Options must be provided as --name <value> pairs.");
  const result = new Map<string, string>();
  for (let index = 0; index < values.length; index += 2) {
    const name = values[index];
    const value = values[index + 1];
    if (!name?.startsWith("--") || value === undefined || result.has(name)) {
      fail("INVALID_ARGUMENTS", `Invalid or duplicate option: ${name ?? "<missing>"}.`);
    }
    result.set(name, value);
  }
  return result;
}

async function requiredFile(options: Map<string, string>, name: string): Promise<string> {
  const value = options.get(name);
  if (!value) fail("FILE_NOT_FOUND", `${name} must reference an existing file.`);
  const path = resolve(value);
  try {
    if (!(await stat(path)).isFile()) fail("FILE_NOT_FOUND", `${name} must reference an existing file.`);
  } catch {
    fail("FILE_NOT_FOUND", `${name} must reference an existing file.`);
  }
  return path;
}

async function main(): Promise<void> {
  const [command, ...values] = process.argv.slice(2);
  if (!command) {
    process.stdout.write(`${JSON.stringify({
      candidate: "typescript",
      runtime: process.version,
      scope: "fiscal-discovery-poc",
      productionReady: false,
    })}\n`);
    return;
  }
  const options = parseOptions(values);
  switch (command) {
    case "validate": return runValidate(options);
    case "generate": return runGenerate(options);
    case "check-key": return runCheckKey(options);
    case "sign": return runSign(options);
    case "verify": return runVerify(options);
    case "status": return runStatus(options);
    case "bench": return runBench(options);
    default: fail("UNKNOWN_COMMAND", "Supported commands: validate, generate, check-key, sign, verify, status.");
  }
}

// Nearest ancestor of this source file that contains .git; none in a container.
function repositoryRoot(): string | null {
  for (let current = dirname(fileURLToPath(import.meta.url)); ; current = dirname(current)) {
    if (existsSync(resolve(current, ".git"))) return current.toLowerCase() + sep;
    if (dirname(current) === current) return null;
  }
}

function insideRepository(path: string): boolean {
  const root = repositoryRoot();
  return root !== null && resolve(path).toLowerCase().startsWith(root);
}

function sha256(data: string | Buffer): string {
  return createHash("sha256").update(data).digest("hex");
}

async function runGenerate(options: Map<string, string>): Promise<void> {
  const load = async (name: string) => JSON.parse(await readFile(await requiredFile(options, name), "utf8"));
  const fixture = await load("--case");
  const profile = await load("--profile");
  const issuer = await load("--issuer");
  const synthetic = issuer.cnpj === "00000000000000";
  let result;
  try {
    result = generate(fixture, profile, issuer, Number(options.get("--nnf")), options.get("--cnf") ?? "", options.get("--dhemi") ?? "");
  } catch (error) {
    if (error instanceof RefusedError) fail("GENERATION_REFUSED", error.message);
    throw error;
  }
  const out = options.get("--out");
  if (out) {
    if (!synthetic && insideRepository(out)) fail("OUTPUT_INSIDE_REPOSITORY", "A document for a real issuer carries taxpayer data; write it outside the repository.");
    await writeFile(resolve(out), result.xml, "utf8");
  } else if (!synthetic) {
    fail("OUT_REQUIRED", "A real issuer requires --out outside the repository; the XML is not printed.");
  }
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript", command: "generate", profile: profile.profileId, syntheticIssuer: synthetic,
    accessKey: synthetic ? result.accessKey : null, xmlSha256: sha256(Buffer.from(result.xml, "utf8")),
    bytes: Buffer.byteLength(result.xml, "utf8"),
  })}\n`);
}

function runCheckKey(options: Map<string, string>): void {
  const key = options.get("--key") ?? "";
  if (!/^\d{44}$/.test(key)) fail("INVALID_KEY", "--key must be 44 digits.");
  const valid = accessKeyCheckDigit(key.slice(0, 43)) === Number(key[43]);
  process.stdout.write(`${JSON.stringify({ candidate: "typescript", command: "check-key", checkDigitValid: valid })}\n`);
}

async function loadIssuerA1(options: Map<string, string>) {
  const pfxPath = await requiredFile(options, "--pfx");
  if (insideRepository(pfxPath)) fail("A1_INSIDE_REPOSITORY", "The A1 file must live outside the repository working tree.");
  const password = process.env.RRF5_A1_PASSWORD;
  if (!password) fail("A1_PASSWORD_MISSING", "Set RRF5_A1_PASSWORD; the password is never accepted as an argument.");
  const pfx = await readFile(pfxPath);
  let a1;
  try {
    a1 = loadA1(pfx, password);
  } catch (error) {
    fail("A1_LOAD_FAILED", error instanceof Error ? error.message : String(error));
  }
  const now = new Date();
  if (now < a1.notBefore || now > a1.notAfter) fail("A1_OUT_OF_VALIDITY", "Certificate outside its validity window.");
  return { a1, pfx, password };
}

async function runSign(options: Map<string, string>): Promise<void> {
  const { a1 } = await loadIssuerA1(options);
  const input = options.get("--xml");
  const out = options.get("--out");
  if (input && !out) fail("OUT_REQUIRED", "Signing a document requires --out outside the repository.");
  const xml = input
    ? await readFile(await requiredFile(options, "--xml"), "utf8")
    : '<NFe xmlns="http://www.portalfiscal.inf.br/nfe"><infNFe Id="NFe00000000000000000000000000000000000000000000" versao="4.00">'
      + "<poc>SYNTHETIC NON-FISCAL XMLDSIG TEST - NO TAX CONTENT</poc><vTest>10.00</vTest></infNFe></NFe>";
  const signed = sign(xml, a1);
  const verification = verify(signed, a1.certificateDer);
  const mutation = verify(mutateTotal(signed, Boolean(input)), a1.certificateDer);
  if (out) {
    if (insideRepository(out)) fail("OUTPUT_INSIDE_REPOSITORY", "Signed XML embeds the A1 public certificate; write it outside the repository.");
    await writeFile(resolve(out), signed, "utf8");
  }
  const mutationRejected = !mutation.verified && mutation.detail === "cryptographic";
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript", command: "sign", runtime: process.version, scope: input ? "generated-nfe-document" : "synthetic-non-fiscal-xml",
    signatureMethod: algorithms.RSA_SHA1, digestMethod: algorithms.SHA1, canonicalization: algorithms.C14N,
    certificateSha256: a1.sha256, signatureVerified: verification.verified, verificationDetail: verification.detail,
    mutationRejected, mutationDetail: mutation.detail,
  })}\n`);
  if (!verification.verified || !mutationRejected) process.exitCode = 2;
}

async function runVerify(options: Map<string, string>): Promise<void> {
  const path = await requiredFile(options, "--xml");
  if (insideRepository(path)) fail("XML_INSIDE_REPOSITORY", "Fiscal XML carries taxpayer data; keep it outside the repository.");
  const expected = (options.get("--cert-sha256") ?? "").toLowerCase();
  if (expected.length !== 64) fail("FINGERPRINT_REQUIRED", "--cert-sha256 must be the expected 64-hex A1 SHA-256 fingerprint.");
  const bytes = await readFile(path);
  const xml = bytes.toString("utf8");
  const certificate = embeddedCertificate(xml);
  const verification = verify(xml, certificate);
  const mutation = verify(mutateTotal(xml, true), certificate);
  const digest = /<DigestValue>([^<]*)<\/DigestValue>/.exec(xml)?.[1];
  const protocolDigest = /<digVal>([^<]*)<\/digVal>/.exec(xml)?.[1];
  const certificateSha256Matches = sha256(certificate) === expected;
  const mutationRejected = !mutation.verified && mutation.detail === "cryptographic";
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript", command: "verify", runtime: process.version, fileSha256: sha256(bytes),
    certificateSha256Matches, signatureVerified: verification.verified, verificationDetail: verification.detail,
    mutationRejected, mutationDetail: mutation.detail,
    protocolDigestMatches: digest !== undefined && digest === protocolDigest,
    cStat: /<infProt>.*?<cStat>([^<]*)<\/cStat>/s.exec(xml)?.[1] ?? null,
  })}\n`);
  if (!certificateSha256Matches || !verification.verified || !mutationRejected) process.exitCode = 2;
}

// Performance comparison: generate -> sign -> verify per iteration with a fresh nNF,
// after a warm-up. Synthetic inputs and a throwaway key only.
async function runBench(options: Map<string, string>): Promise<void> {
  const load = async (name: string) => JSON.parse(await readFile(await requiredFile(options, name), "utf8"));
  const fixture = await load("--case");
  const profile = await load("--profile");
  const issuer = await load("--issuer");
  const iterations = Number(options.get("--iterations") ?? "2000");
  const warmup = Number(options.get("--warmup") ?? "200");
  const { a1 } = await loadIssuerA1(options);
  const once = (n: number) => {
    const result = generate(fixture, profile, issuer, n, "12345678", "2026-11-06T12:00:00-03:00");
    return verify(sign(result.xml, a1), a1.certificateDer).verified;
  };
  for (let i = 1; i <= warmup; i++) if (!once(i)) fail("BENCH_VERIFY_FAILED", "Warm-up iteration failed verification.");
  const samples: number[] = [];
  const total = process.hrtime.bigint();
  for (let i = 0; i < iterations; i++) {
    const started = process.hrtime.bigint();
    if (!once(warmup + 1 + i)) fail("BENCH_VERIFY_FAILED", "Iteration failed verification.");
    samples.push(Number(process.hrtime.bigint() - started) / 1e6);
  }
  const seconds = Number(process.hrtime.bigint() - total) / 1e9;
  samples.sort((a, b) => a - b);
  const percentile = (p: number) => samples[Math.min(samples.length - 1, Math.ceil(p * samples.length) - 1)]!;
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript", command: "bench", runtime: process.version, iterations, warmup,
    opsPerSecond: Math.round((iterations / seconds) * 10) / 10,
    p50Ms: Number(percentile(0.5).toFixed(3)), p95Ms: Number(percentile(0.95).toFixed(3)), p99Ms: Number(percentile(0.99).toFixed(3)),
    peakRssMb: Math.round(process.resourceUsage().maxRSS / 1024 * 10) / 10,
  })}\n`);
}

// Read-only NFeStatusServico4 probe. Node's TLS stack accepts the PFX directly.
// Trust: Node ships its own Mozilla CA bundle (not the OS store); with
// RRF5_SEFAZ_TRUST_ROOT the `ca` option replaces it with the pinned ICP-Brasil v10 root.
const ICP_BRASIL_V10_SHA256 = "6e0bff069a26994c15de2c4888cc54af84882e5495b7fbf66be9ccffec7489f6";

async function runStatus(options: Map<string, string>): Promise<void> {
  const { a1, pfx, password } = await loadIssuerA1(options);
  let ca: string | undefined;
  const rootPath = process.env.RRF5_SEFAZ_TRUST_ROOT;
  if (rootPath) {
    const root = new X509Certificate(await readFile(rootPath));
    if (sha256(root.raw) !== ICP_BRASIL_V10_SHA256) fail("TRUST_ROOT_REFUSED", "RRF5_SEFAZ_TRUST_ROOT is not the pinned ICP-Brasil v10 root.");
    ca = root.toString();
  }
  const envelope = '<soap12:Envelope xmlns:soap12="http://www.w3.org/2003/05/soap-envelope"><soap12:Body>'
    + '<nfeDadosMsg xmlns="http://www.portalfiscal.inf.br/nfe/wsdl/NFeStatusServico4">'
    + '<consStatServ versao="4.00" xmlns="http://www.portalfiscal.inf.br/nfe"><tpAmb>2</tpAmb><cUF>35</cUF><xServ>STATUS</xServ></consStatServ>'
    + "</nfeDadosMsg></soap12:Body></soap12:Envelope>";
  const started = Date.now();
  const trustRoot = ca ? "pinned" : "node-bundled";
  let response: { status: number; body: string };
  try {
    response = await new Promise((resolvePromise, reject) => {
      const req = request("https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeStatusServico4.asmx", {
        method: "POST", pfx, passphrase: password, ca, minVersion: "TLSv1.2", timeout: 30_000,
        headers: { "Content-Type": "application/soap+xml; charset=utf-8" },
      }, (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (chunk: Buffer) => chunks.push(chunk));
        res.on("end", () => resolvePromise({ status: res.statusCode ?? 0, body: Buffer.concat(chunks).toString("utf8") }));
      });
      req.on("timeout", () => req.destroy(new Error("timeout")));
      req.on("error", reject);
      req.end(envelope);
    });
  } catch (error) {
    fail("SEFAZ_TRANSPORT_FAILED", `${Date.now() - started} ms; trustRoot=${trustRoot}; ${error instanceof Error ? error.message : String(error)}`);
  }
  const field = (name: string) => new RegExp(`<${name}>([^<]*)</${name}>`).exec(response.body)?.[1] ?? null;
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript", command: "status", httpStatus: response.status, elapsedMs: Date.now() - started,
    certificateSha256: a1.sha256, trustRoot,
    sefaz: field("cStat") ? { tpAmb: field("tpAmb"), verAplic: field("verAplic"), cStat: field("cStat"), xMotivo: field("xMotivo") } : null,
  })}\n`);
  if (!field("cStat")) process.exitCode = 2;
}

async function runValidate(options: Map<string, string>): Promise<void> {
  const xmlPath = await requiredFile(options, "--xml");
  const xsdPath = await requiredFile(options, "--xsd");
  const packagePath = await requiredFile(options, "--package");
  const packageHash = createHash("sha256").update(await readFile(packagePath)).digest("hex");
  if (packageHash !== EXPECTED_PACKAGE_SHA256) {
    fail("XSD_PACKAGE_HASH_MISMATCH", `Expected ${EXPECTED_PACKAGE_SHA256}, got ${packageHash}.`);
  }

  const schemaDirectory = dirname(xsdPath);
  const schemaNames = (await readdir(schemaDirectory)).filter((name) => name.toLowerCase().endsWith(".xsd"));
  const schemaFiles: XMLFileInfo[] = await Promise.all(
    schemaNames.map(async (name) => ({ fileName: name, contents: await readFile(resolve(schemaDirectory, name), "utf8") })),
  );
  const mainSchema = schemaFiles.find((file) => file.fileName === basename(xsdPath));
  if (!mainSchema) fail("XSD_NOT_PRELOADED", "The selected XSD was not found in its schema directory.");

  const result = await validateXML({
    xml: [{ fileName: basename(xmlPath), contents: await readFile(xmlPath, "utf8") }],
    schema: [mainSchema],
    preload: schemaFiles.filter((file) => file !== mainSchema),
  });
  process.stdout.write(`${JSON.stringify({
    candidate: "typescript",
    valid: result.valid,
    schema: mainSchema.fileName,
    packageSha256: packageHash,
    messages: result.errors.map((error) => ({ message: error.message, location: error.loc })),
  })}\n`);
  if (!result.valid) process.exitCode = 2;
}

main().catch((error: unknown) => fail("VALIDATION_ERROR", error instanceof Error ? error.message : String(error)));
