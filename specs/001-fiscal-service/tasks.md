# Tasks: Fiscal Service Discovery

**Branch**: `feature/001-fiscal-discovery`

**Goal**: concluir pesquisa e POCs necessárias para decidir stack/biblioteca e liberar a fundação, sem código produtivo.

## Phase 1 — Discovery setup

- [x] T001 Create discovery evidence directories in `pocs/evidence/`, `pocs/fixtures/` and `pocs/scorecards/`
- [x] T002 Add repository security and secret-handling policy in `SECURITY.md`
- [x] T003 Add contribution and Git Flow conventions in `CONTRIBUTING.md`
- [x] T004 [P] Add official-reference catalog schema in `regulatory/catalog.schema.json`
- [x] T005 [P] Add sanitized fiscal-case schema in `tests/FiscalCases/fiscal-case.schema.json`
- [x] T006 Add CI secret scan and documentation validation workflow in `.github/workflows/discovery-validation.yml`

## Phase 2 — Normative baseline

- [x] T007 Populate dated NF-e/NFC-e official reference catalog in `regulatory/catalog.json`
- [x] T008 Record hashes and applicability for MOC, XSD, NT 2025.002, DANFE/QR and contingency in `pocs/evidence/normative-baseline.md`
- [x] T009 [P] Document SEFAZ-SP homologation services and capabilities in `pocs/evidence/sefaz-sp-endpoints.md`
- [x] T010 [P] Create RTC impact checklist in `pocs/evidence/rtc-impact.md`
- [ ] T011 Review normative baseline with responsible fiscal stakeholder and record approval in `pocs/evidence/fiscal-review.md`

## Phase 3 — US1 emission feasibility POCs

- [x] T012 [P] [US1] Scaffold isolated .NET 10 POC in `pocs/dotnet/`
- [x] T013 [P] [US1] Scaffold isolated Java 25 POC in `pocs/java/`
- [x] T014 [P] [US1] Scaffold TypeScript baseline POC in `pocs/typescript/`
- [x] T015 [US1] Create one normalized sanitized NFC-e fixture in `pocs/fixtures/nfce-minimal.json`
- [ ] T016 [US1] Implement XML generation and official XSD validation in each candidate under `pocs/*/`
- [ ] T017 [US1] Implement A1 loading, XML signing, signature verification and mutation-negative test in `pocs/*/`
- [ ] T018 [US1] Implement mock SOAP authorization and sanitized tracing in `pocs/*/`
- [x] T019 [US1] Execute controlled SEFAZ-SP homologation test and record evidence in `pocs/evidence/homologation.md`
- [ ] T020 [US1] Validate DANFE/QR capability or blocking gaps in `pocs/evidence/danfe-qr.md`

## Phase 4 — US2 contingency and uncertain-result design

- [x] T021 [P] [US2] Model PDV-to-service connectivity failure and local-agent alternatives in `pocs/evidence/offline-agent-spike.md`
- [x] T022 [US2] Implement authorization-timeout simulation with consult-before-retry in `pocs/shared/uncertain-result/`
- [x] T023 [US2] Record official contingency applicability and non-goals in `pocs/evidence/contingency-gates.md`

## Phase 5 — US3 security and multi-tenant foundation proof

- [x] T024 [P] [US3] Create threat model for A1, CSC, tenant, webhook and storage in `pocs/evidence/threat-model.md`
- [x] T025 [US3] Prove secrets stay outside source/log/container layers in `pocs/evidence/secret-handling.md`
- [x] T026 [US3] Design tenant/environment/series database constraints in `specs/001-fiscal-service/data-model.md`

## Phase 6 — US5 integration contract validation

- [x] T027 [P] [US5] Validate OpenAPI 3.1 and generate sample client in `pocs/contract/`
- [x] T028 [US5] Review BurgoOS idempotency, webhook and polling flow in `pocs/evidence/burgoos-contract-review.md`

## Phase 7 — US6 decision and handoff

- [x] T029 [US6] Complete weighted candidate scorecard in `pocs/scorecards/runtime-library-scorecard.md`
- [ ] T030 [US6] Produce license/SBOM/maintenance review in `pocs/evidence/dependency-review.md`
- [ ] T031 [US6] Update `docs/fiscal-service/adr/ADR-011-fiscal-library.md` with the accepted library strategy
- [ ] T032 [US6] Update `docs/fiscal-service/adr/ADR-012-technology-stack.md` with the accepted stack
- [ ] T033 [US6] Record POC limitations and external blockers in `specs/001-fiscal-service/discovery-results.md`
- [ ] T034 [US6] Create the foundation entry checklist in `specs/001-fiscal-service/checklists/foundation-entry.md`

## Phase 8 — Quality gate

- [ ] T035 Run all POC tests, secret scans, OpenAPI validation and container builds documented in `specs/001-fiscal-service/quickstart.md`
- [ ] T036 Verify no test certificate, CSC, XML with PII or credential is tracked using `pocs/evidence/repository-audit.md`
- [ ] T037 Review all discovery evidence and approve merge to `develop` in `specs/001-fiscal-service/discovery-signoff.md`

## Dependencies

`T001-T011` establish sources and security. `T012-T020` prove US1 and block final stack choice. `T021-T026` can proceed after the common fixture and normative baseline. `T027-T028` can run in parallel with technical POCs. `T029-T037` require all evidence or explicit external-blocker records.

## MVP for this branch

The branch is complete when one candidate passes all eliminatory gates, ADR-011/012 are accepted, homologation evidence exists (or is explicitly externally blocked), and the foundation checklist is approved. No production emission endpoint is delivered here.
