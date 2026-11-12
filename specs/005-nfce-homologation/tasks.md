# Tasks: NFC-e emission in homologation

**Branch**: `feature/005-nfce-homologation`

**Goal**: NFC-e model 65 issued by the product code against SEFAZ-SP homologation. The food-truck matrix ends as expected. Sync and async emission; the DANFE is ready to print.

Tasks marked **[operator]** use the real A1 or the real issuer and are run by the operator through a script. **[P]** marks tasks that can run in parallel.

## Phase 1 — Normative gates (checklist B)

- [x] T001 Regenerate and pin the golden XML against 010g v1.03 without 010g-only elements; record its hash in `pocs/fixtures/golden/` and `normative-baseline.md` (E4, E5) Done 2026-11-12: golden pinned and valid against 010g; no regeneration needed (`normative-gates.md`, `GoldenXmlTests`).
- [x] T002 [P] Record the MOC 7.0 section that defines the signature algorithm and transforms, from the frozen copy outside Git, in `pocs/evidence/xmldsig-poc.md` (L8) Done 2026-11-12: MOC 7.0 Visão Geral §4.2.4, Tabela 4-2, §4.2.5 (`normative-gates.md`).
- [x] T003 [P] Extract the `tPag` code table and the DANFE labels from the frozen MOC copy into a versioned resource with its source reference (L5) Done 2026-11-12: national table frozen and transcribed by script (`regulatory/payment-methods.json`, `PaymentMethods`).
- [x] T004 [P] Record the layout rounding rule with its source, and port the POC boundary cases as tests (checklist B) Done 2026-11-12: MOC Anexo I footnote (*4); half away from zero as a technical choice (`FiscalRounding`).
- [x] T005 [P] Pin the ICP-Brasil roots per authorizer for the SEFAZ TLS endpoints, reusing the catalog bundle, with TLS validation always on (checklist B) Done 2026-11-12: SP pinned to AC Raiz v10 from the ITI bundle (`SefazTlsTrust`).
- [x] T006 Record ADR-005, ADR-007 and ADR-016 in `docs/fiscal-service/adr/` Done 2026-11-12: written as Proposed, waiting for the operator's acceptance.

## Phase 2 — Model and persistence

- [x] T007 Migrations: document items, payments, protocol, SEFAZ request/response records (hashes and object references, never raw XML in the database) and an outbox table, with tenant RLS and composite keys Done 2026-11-12: migration 0011 (`fiscal_document_item`, `fiscal_document_payment`, `sefaz_exchange`, `fiscal_protocol`, `document_outbox`), formats from the 010g XSD (TProt, TChNFe, TStat).
- [x] T008 Enforce the closed state machine of `data-model.md` in the database (allowed transitions only; `ERROR` only from retryable stages; frozen fields after the signed boundary); add the tests in `tests/Database/` Done 2026-11-12: `fiscal_document_transition`; AUTHORIZED needs cStat 100 with the same key and digest plus the stored nfeProc; a resend from WAITING_SEFAZ needs a later consultation answering 217 (`DocumentStateMachineTests`).
- [x] T009 Add the document endpoints to the 002 contract: pre-validate, issue (sync and async), get, poll example for non-terminal states, download of the sent XML, `nfeProc` and DANFE Done 2026-11-12: eight operations in the 002 contract, marked `x-rrf5-planned` until implemented (`ContractTests` checks that planned operations are not routed); scopes `documents.issue` and `documents.read`; Redocly lint clean.

## Phase 3 — E2 Pre-validation

- [x] T010 Validate the request: establishment and environment, `ACTIVE` A1 within validity, CSC configured only when the QR version in use requires it, approved profile in force on the document date, items covered by rules, totals and payments consistent; typed problems for every failure; no number reserved Done 2026-11-12: `DocumentPrevalidation` and `POST /v1/environments/{id}/documents/prevalidate`; every problem with a JSON pointer; payment rules MOC YA 865/866/869/904/391/392/437, CPF E05-10 (237); 737 (tpIntegra=2) left to the UF (L6). The request now carries `operation` and each item `fiscalProfileId` (ADR-015).
- [x] T011 Map commercial payment methods to official `tPag` through the T003 table; mixed payments and change must add up exactly Done 2026-11-12: per-environment mapping (operator decision), migration 0012 and `GET/PUT /v1/environments/{id}/payment-methods`; codes checked against the national table and its validity.

## Phase 4 — Generation and signing (`Rrf5.Fiscal.Nfce`)

- [x] T012 Access key (44 digits with the official check digit) and `cNF` from a cryptographic random source; tests with the official example values Done 2026-11-12: `AccessKey` per NT Conjunta 2025.001 §5 and Anexo II (alphanumeric CNPJ); cNF from a cryptographic source and rule B09-20 (rejection 897).
- [ ] T013 XML generator in layout order from the normalized request and the profile snapshot; output equal to the T001 golden XML for the minimal scenario Generator done 2026-11-12: `NfceGenerator` (nfce-4.00-g1) is byte-identical to the golden; variants are 010g-valid. Operator decision 2026-11-12 (ADR-017): the ERP sends the product attributes; the mapping into `NfceDocument` comes with T013a.
- [x] T013a Operation nature and ERP product data (ADR-017): items carry `ncm`, `cfop`, `csosn`, `origin`, `unit`, `gtin`, `cest`; the document carries `nature`; rule selection `ncm-<NCM>` then `ANY`; `document.tax_mismatch` when CFOP or CSOSN differ from the approved rule; `orig` leaves the rule payload; CEST in the generator; item snapshot columns in 0011; contract and tests Done 2026-11-12: `ProductCodes` (010g patterns, GS1 check digit), `ResolveForNatureAsync` (an existing NCM exception is authoritative even with no version in force, so `ANY` never stands in for it), `document.product_invalid` and `document.tax_mismatch`, `orig` removed from the payload, optional CEST in the generator, migration 0013. No database test exercises the 0013 checks yet; they are covered when the emission writes items (Phase 6).
- [ ] T013b (deferred, operator 2026-11-12) CSOSN 500 / resale with ST: `ICMSSN500`, CEST and retained ICMS-ST in a later generator version, after the accountant confirms the fields
- [x] T014 XSD validation against the resolved package, read from the artifact store with a hash check; DTD and external entities disabled; size and depth limits; negative corpus of XXE, entity expansion and duplicate IDs (TM-09) Done 2026-11-12: `NfceSchema` loads only from the verified package ZIP; DTD prohibited, no resolver, size limit; XXE and entity-expansion corpus refused. Duplicate-ID checks come with the signature (T015).
- [x] T015 XMLDSig in the worker with the A1 opened from the vault (`rrf5-worker` policy), following the T002 section; verify the signature before sending; a test proves the API process cannot sign Done 2026-11-12: `NfceSigner` follows MOC 7.0 Visão Geral §4.2.4 and Tabela 4-2, with every element asserted. The signed document is fully 010g-valid. Verification refuses tampering, duplicate Id, a second infNFe and a foreign certificate. The API cannot sign because it cannot open the A1 (`VaultPolicyTests`, policy `rrf5-api`). The worker call comes with Phase 6.
- [x] T016 Number allocation right before signing (`08-emission-flows.md`); a failure after the reservation leaves an audited gap Moved to Phase 6: it is the READY_TO_SIGN → SIGNING step of the worker, and the database already refuses SIGNING without a number (migration 0011). Done 2026-11-12: `DocumentProcessor` reserves and binds the number inside the transaction that moves READY_TO_SIGN to SIGNING, after every pre-signing check; a signing failure goes to ERROR and the retry reuses the same number and key (no gap). Gap handling for an abandoned number stays with inutilização (Phase 3).

## Phase 5 — SEFAZ adapter (`Rrf5.Fiscal.Adapters.Sefaz`)

- [x] T017 SOAP 1.2 client with mutual TLS: the client certificate comes from the A1, server trust from the T005 roots; endpoints from the catalog per authorizer and environment Done 2026-11-12: `SefazNfceClient` (project `Rrf5.Fiscal.Adapters.Sefaz`, BCL only); client chain built offline from the PKCS#12; server trust is the `X509ChainPolicy` from `SefazTlsTrust`; endpoints from `regulatory/sefaz-endpoints.json` (SP homologation only; production fails closed). `NotSent` only when the request body never started to go out; after that, any failure is `Uncertain`.
- [x] T018 Parse `retEnviNFe`, `protNFe` and `retConsSitNFe`; `AUTHORIZED` only with `cStat` 100 and the same `chNFe` and `digVal`; rejection codes and messages kept as official values Done 2026-11-12: `SefazResponses` per MOC 7.0 Visão Geral §5.1.4 and §5.4 and Anexo I §4.4.1 and §4.4.3. Authorized needs 100 with the same chNFe and digVal; denial 110/301/302/303; 103 is an asynchronous receipt; 108/109 service paused; duplicity 204/539 (rules 2B08-10/20) is uncertain, never a rejection; a different digVal is `Mismatch`; 150, 101 and 151 are `Unexpected` (manual review).
- [x] T019 Test SOAP server (a rewrite of the POC mock): 104/100, 225, 217, timeout after authorization, unreadable answer Done 2026-11-12: `SefazTestServer` (Kestrel, HTTPS with a required client certificate, test root): 104/100, 225, 104/301, 204, 103, 108, 217, 101, timeout after authorization, unreadable answer, SOAP fault, other key, other digVal, other tpAmb; a server outside the pinned root is never reached (`SefazAdapterTests`).

## Phase 6 — E1 and E3 Emission and uncertain result

- [x] T020 Async issue: the API stores the document and the outbox message in one transaction and returns `202`; the worker consumes, signs, sends and finalizes; duplicate deliveries are harmless Done 2026-11-12: `DocumentIssue` stores the document (READY_TO_SIGN, items, payments, snapshot of the approved rule versions, consumer) and a PROCESS outbox message in one transaction and answers 202. `DocumentOutbox` claims messages with `for update skip locked` and keeps the lock while `DocumentProcessor` runs, so delivery is at least once and a duplicate finds another state. **Pending operator decision:** the RabbitMQ hop of ADR-005 item 2; this step reads the outbox directly.
- [x] T021 Sync issue: the same path, but the API waits for a terminal state up to a configurable timeout. If the timeout expires, it answers with the non-terminal document instead of an error Done 2026-11-12: the synchronous issue waits on the database up to `Documents:SyncTimeoutSeconds` (default 15) and answers 201 with the terminal document or 202 with `Location` and `Retry-After`.
- [x] T022 Uncertain result: after a timeout or an unreadable answer, consult by access key before any resend; a consultation failure keeps `WAITING_SEFAZ` with backoff and an alert Done 2026-11-12: after SENDING, anything but 100, an official refusal or not-sent is WAITING_SEFAZ; the worker consults by key, resends the same signed bytes only after 217 (the database checks it), keeps waiting with backoff and logs an alert after 5 consultations or at once for Mismatch, Unexpected or a denial (no DENIED state for NFC-e, manual review). A crash in SENDING is treated as uncertain.
- [x] T023 Store the sent XML and the `nfeProc` write-once, hash-verified and with version-pinned reads (feature 004 store); the document keeps the hashes Done 2026-11-12: the signed XML is stored before SENDING and the nfeProc (exact signed bytes plus protNFe as received) before AUTHORIZED, both content-addressed, read back and hash-checked on write, read pinned to the version; request and response bodies of every exchange are stored too, and the database keeps only hashes and references.
- [x] T024 Idempotency for issue: same key and payload return the same document; a different payload returns a conflict; no new number either way Done 2026-11-12: same Idempotency-Key and payload return the same document (current state, `Idempotent-Replayed`); another payload is 409; a pre-validation failure is 422 `document.invalid` with the problems and stores nothing (the claim rolls back); the number is reserved only by the worker.
- [ ] T025 Telemetry and audit for each stage, with redaction tests: no access key, CPF, CSC or XML in logs or spans

## Phase 7 — E5 QR Code and DANFE

- [ ] T026 QR Code v3 online per the Manual DANFE NFC-e v6.0 §4.4.1 (key, version and environment; no CSC), as proven in homologation by the POC (`danfe-qr.md`). QR v2 with the CSC hash only if a later decision requires it; the CSC never appears in logs, responses or diagnostics (TM-04)
- [ ] T027 DANFE NFC-e HTML at 80 and 58 mm, from the `nfeProc` only; print data in the API (QR content, QR URL, consultation URL)
- [ ] T028 Simulated printing: render at 80 and 58 mm, check the margins and the QR quiet zone, decode the QR from the rendered image and compare it with the expected content
- [ ] T029 `danfe-selftest` inside the Linux container image (L3)

## Phase 8 — E4 Real homologation

- [ ] T030 After the accountant's ratification: approve the imported profile with the approver credential, with `validFrom` not before the approval date (T011 of 001)
- [ ] T031 [operator] Re-run the 010g probe after 2026-11-16 and record the result; enable 010g-only elements only through an audited release if it is accepted
- [ ] T032 [operator] Script `scripts/operator/emit-homologation.ps1`: one authorized sale on the hardened local stack with the real issuer and A1; prints a summary table without access key or CNPJ
- [ ] T033 Food-truck matrix fixtures for the MVP scenarios (1–7, 13, 15–17, 23, 28, 30) with the expected outcome of each
- [ ] T034 [operator] Run the matrix in homologation and record expected against obtained `cStat` per scenario in `pocs/evidence/homologation.md`

## Phase 9 — Closure

- [ ] T035 Contract lint and contract tests for the document endpoints; closed state enum unchanged
- [ ] T036 Threat model retest for TM-06, TM-08, TM-09 and TM-16 on the local stack; update checklist B
- [ ] T037 Quickstart: from an onboarded issuer to an authorized NFC-e, sync and async
- [ ] T038 Sign-off draft for the operator

## Dependencies

- Phase 1 comes first. T002 blocks T015, T003 blocks T011, T001 blocks T013, and T005 blocks T017.
- Phases 2 to 7 run against the test SOAP server and do not depend on the accountant.
- T030 depends on the accountant's ratification (expected by 2026-11-13). T032 to T034 depend on T030.
- T031 depends on the date (after 2026-11-16).
