# OpenAPI contract POC

This POC validates the OpenAPI 3.1 contract, generates TypeScript types and
type-checks a minimal client without contacting a service.

```powershell
npm install
npm run generate
npm run check
```

Tool versions are pinned in `package-lock.json`. `check:generated` fails when the
committed types drift from the source contract. The sample client requires the
caller to supply an access-token resolver and an idempotency key; it never stores
credentials or silently retries a fiscal mutation.

The example consumes the sanitized `nfce-minimal.json` fixture. It exports a
function but does not invoke it, so CI does not need network access, taxpayer
data or a credential.

## Evidence from 2026-08-18

- Redocly structural/spec lint: pass after correcting one malformed flow-style
  response description in the source contract;
- `openapi-typescript` generation: pass;
- strict TypeScript check of the generated client: pass;
- runtime dependency audit (`npm audit --omit=dev`): zero vulnerabilities.

Redocly v1.34.19 is pinned for the Node 20 discovery baseline. Its transitive
development tree has known audit findings recorded in T030; therefore it may
process only the trusted repository contract and is not an approved runtime
dependency. Migration to Redocly v2 requires Node 22. The CLI is not shipped
with, imported by or reachable from the generated runtime client.
