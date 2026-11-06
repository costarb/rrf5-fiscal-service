import { createHash } from "node:crypto";
import { readdir, readFile, stat } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { validateXML, type XMLFileInfo } from "xmllint-wasm";

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
  if (command !== "validate") fail("UNKNOWN_COMMAND", "Only the validate command is implemented in this T016 increment.");

  const options = parseOptions(values);
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
