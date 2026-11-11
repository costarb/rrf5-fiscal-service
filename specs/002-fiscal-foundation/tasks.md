# Tasks: Fiscal Service Foundation

**Branch**: `feature/002-fiscal-foundation`

**Goal**: base de produto com dois tenants em onboarding completo, cofre, perfis fiscais aprovados e seleção de schema, sem emissão.

## Phase 1 — Setup

- [x] T001 Create the .NET 10 solution and projects listed in `specs/002-fiscal-foundation/plan.md` with nullable, warnings as errors and central package versions in `Directory.Packages.props`
- [x] T002 Add the local stack (PostgreSQL, SeaweedFS, RabbitMQ, OpenBao dev) in `deploy/compose/docker-compose.yml` with a one-command start documented in `specs/002-fiscal-foundation/quickstart.md`
- [x] T003 Add the chiseled, non-root API/Worker image with working `America/Sao_Paulo` in `deploy/docker/Dockerfile` (L3)
- [x] T004 Extend CI in `.github/workflows/` with build, unit and integration tests, image build, CycloneDX SBOM and Trivy scan failing on fixable high/critical (L2)
- [x] T005 [P] Fix the mis-encoded UTF-8 text in `specs/001-fiscal-service/spec.md`
- [x] T006 [P] Record ADR-013 (token issuer), ADR-014 (data access and migrations) and ADR-015 (tax rule model) in `docs/fiscal-service/adr/`

## Phase 2 — Shared foundation

- [x] T007 Write SQL migrations for the `data-model.md` tables, composite keys, checks and immutability triggers in `db/migrations/`
- [x] T008 Enable RLS and transaction-local tenant context; prove absent or foreign context is denied in `tests/Database/`
- [x] T009 Implement the database constraint tests listed in `specs/001-fiscal-service/data-model.md` in `tests/Database/`
- [x] T010 Implement idempotency claim (key + canonical request hash) for administrative mutations in `src/Rrf5.Fiscal.Application/`
- [x] T011 Implement append-only audit log with principal, tenant, resource, action and correlation in `src/Rrf5.Fiscal.Infrastructure/`
- [x] T012 Implement Problem Details with stable domain codes and the redaction allowlist for logs and traces in `src/Rrf5.Fiscal.Api/`
- [x] T013 Wire OpenTelemetry traces and metrics with redaction tests in `tests/Security/`

## Phase 3 — F1 Identity and tenancy

- [x] T014 Implement Tenant and Application administration (create, suspend, rotate secret; secret shown once, stored as hash)
- [x] T015 Implement `/oauth/token` (`client_credentials`), short JWT signed with a OpenBao-held key, per ADR-013
- [x] T016 Implement scopes and application-to-establishment grants; enforce on every endpoint
- [x] T017 Add cross-tenant API tests (guessed IDs return `404`; missing scope returns `403`) in `tests/Security/`

## Phase 4 — F2 Issuer registration and numbering

- [x] T018 Implement Company, Establishment and FiscalEnvironment administration with canonical tax IDs
- [x] T019 Implement FiscalConfiguration (timezone, authorizer catalog version, schema policy) per environment
- [x] T020 Implement FiscalSeries administration and the atomic allocation of `data-model.md` (no decrement path)
- [x] T021 Add 1.000-allocation concurrency test and gap audit test in `tests/Database/`

## Phase 5 — F3 Certificate and CSC vault

- [x] T022 Implement `ICertificateVault` over OpenBao transit (envelope encryption, per-certificate data key)
- [x] T023 Implement A1 upload: PKCS#12 parse, holder tax ID vs establishment, validity window, fingerprint; fail closed when the vault is unavailable
- [x] T024 Implement CSC storage per environment in `ISecretStore`; metadata-only reads; no export endpoint
- [x] T025 Implement daily certificate expiry alerts at 60, 30, 15, 7 and 1 days in `src/Rrf5.Fiscal.Worker/`
- [x] T026 Add canary tests (A1 password, CSC) over responses, logs, traces, database dump and image layers in `tests/Security/`

## Phase 6 — F4 Tax profiles

- [x] T027 Implement TaxProfile, TaxRule (explicit dimensions) and immutable TaxRuleVersion with source references
- [x] T028 Implement the approval flow `DRAFT → PENDING_APPROVAL → APPROVED → RETIRED` with the fiscal approver scope; author cannot approve
- [x] T029 Reject overlapping validity for the same dimensions and retroactive validity over existing documents
- [x] T030 Validate rule payloads against the JSON Schema of supported layout groups; reject unsupported groups with the reason
- [x] T031 Import `pocs/fixtures/fiscal-profile-sp-sn-own-production-v1.json` as a DRAFT, pending accountant ratification (T011 of 001)
- [x] T032 Implement profile resolution by establishment, dimensions and date (approved and in-force only)

## Phase 7 — F5 Regulatory catalog and schema selection

- [x] T033 Load `regulatory/catalog.json` into SchemaVersion; refuse packages without verified artifact hash
- [x] T034 Resolve schema version by authorizer, environment and date; package-exclusive elements disabled unless an audited release enables them (E4)
- [x] T035 Store official artifacts in S3 storage (SeaweedFS locally) with content hash, versioning and object lock where available; verify hash on read

## Phase 8 — Contract and closure

- [x] T036 Write `specs/002-fiscal-foundation/contracts/openapi.yaml` with administrative operations, `401`/`403`/`429` and the closed document state enum (L10)
- [x] T037 Replace the contract lint with Node 22 + Redocly v2 or another OpenAPI 3.1 validator with no high findings (L11)
- [x] T038 Write the two-tenant onboarding walkthrough in `specs/002-fiscal-foundation/quickstart.md` and run it on a clean machine
- [x] T039 Retest the threat model items reachable on the local stack and record the result in `pocs/evidence/threat-model.md`
- [ ] T040 Update `specs/001-fiscal-service/checklists/foundation-entry.md` (section B) with evidence and record sign-off in `specs/002-fiscal-foundation/signoff.md`

## Dependencies

`T001-T006` first. `T007-T013` block every user story. F1 (`T014-T017`) blocks the
others because every call needs a principal. F2, F3 and F4 can then run in
parallel; F5 depends on F2 (`FiscalConfiguration`). `T036-T040` close the feature.
