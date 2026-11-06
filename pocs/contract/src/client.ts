import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./generated/schema.js";

export type CreateDocumentRequest = components["schemas"]["CreateDocumentRequest"];
export type FiscalDocument = components["schemas"]["FiscalDocument"];

export interface FiscalClientOptions {
  baseUrl: string;
  getAccessToken: () => string | Promise<string>;
  fetch?: typeof globalThis.fetch;
}

export function createFiscalClient(options: FiscalClientOptions) {
  const client = createClient<paths>({
    baseUrl: options.baseUrl.replace(/\/$/, ""),
    ...(options.fetch ? { fetch: options.fetch } : {}),
  });

  const authentication: Middleware = {
    async onRequest({ request }) {
      request.headers.set("Authorization", `Bearer ${await options.getAccessToken()}`);
      request.headers.set("Accept", "application/json");
      return request;
    },
  };
  client.use(authentication);

  return {
    async createDocument(input: {
      idempotencyKey: string;
      body: CreateDocumentRequest;
      correlationId?: string;
    }): Promise<FiscalDocument> {
      const { data, error, response } = await client.POST("/documents", {
        params: {
          header: {
            "Idempotency-Key": input.idempotencyKey,
            ...(input.correlationId ? { "X-Correlation-Id": input.correlationId } : {}),
          },
        },
        body: input.body,
      });
      if (error || !data) throw fiscalApiError(response, error);
      return data;
    },

    async getDocument(documentId: string): Promise<FiscalDocument> {
      const { data, error, response } = await client.GET("/documents/{documentId}", {
        params: { path: { documentId } },
      });
      if (error || !data) throw fiscalApiError(response, error);
      return data;
    },

    async findDocumentsByExternalId(externalId: string): Promise<FiscalDocument[]> {
      const { data, error, response } = await client.GET("/documents", {
        params: { query: { externalId } },
      });
      if (error || !data) throw fiscalApiError(response, error);
      return data;
    },
  };
}

function fiscalApiError(response: Response, body: unknown): Error {
  const error = new Error(`Fiscal API request failed with HTTP ${response.status}`);
  Object.assign(error, { status: response.status, body });
  return error;
}
