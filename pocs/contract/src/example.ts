import { readFile } from "node:fs/promises";
import { createFiscalClient, type CreateDocumentRequest } from "./client.js";

// Example only. Tokens are resolved at call time and never embedded in source.
const client = createFiscalClient({
  baseUrl: process.env.FISCAL_API_URL ?? "https://sandbox.fiscal.rrf5.example/api/v1",
  getAccessToken: () => {
    const token = process.env.FISCAL_API_TOKEN;
    if (!token) throw new Error("FISCAL_API_TOKEN is required");
    return token;
  },
});

const fixtureUrl = new URL("../../fixtures/nfce-minimal.json", import.meta.url);
const request = JSON.parse(await readFile(fixtureUrl, "utf8")) as CreateDocumentRequest;

// Deliberately not executed during CI: no real service or credential is required.
export async function emitExample() {
  return client.createDocument({
    idempotencyKey: "POC-NFCE-MINIMAL-001",
    correlationId: "POC-CONTRACT-001",
    body: request,
  });
}
