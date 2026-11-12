# Tasks: NFC-e emission in homologation

**Branch**: `feature/005-nfce-homologation`

**Goal**: NFC-e model 65 issued by the product code against SEFAZ-SP homologation. The food-truck matrix ends as expected. Sync and async emission; the DANFE is ready to print.

Tasks marked **[operator]** use the real A1 or the real issuer and are run by the operator through a script. **[P]** marks tasks that can run in parallel.

## Phase 1 — Normative gates (checklist B)

- [x] T001 Regenerate and pin the golden XML against 010g v1.03 without 010g-only elements; record its hash in `pocs/fixtures/golden/` and `normative-baseline.md` (E4, E5) Done 2026-11-12: golden pinned and valid against 010g; no regeneration needed (`normative-gates.md`, `GoldenXmlTests`).
- [x] T002 [P] Record the MOC 7.0 section that defines the signature algorithm and transforms, from the frozen copy outside Git, in `pocs/evidence/xmldsig-poc.md` (L8) Done 2026-11-12: MOC 7.0 Visão Geral §4.2.4, Tabela 4-2, §4.2.5 (`normative-gates.md`).
- [ ] T003 [P] Extract the `tPag` code table and the DANFE labels from the frozen MOC copy into a versioned resource with its source reference (L5) **Blocked:** needs Informe Técnico 2026.001 (named by NT 2026.006) and IT 2024.002 (`normative-gates.md`).
- [x] T004 [P] Record the layout rounding rule with its source, and port the POC boundary cases as tests (checklist B) Done 2026-11-12: MOC Anexo I footnote (*4); half away from zero as a technical choice (`FiscalRounding`).
- [x] T005 [P] Pin the ICP-Brasil roots per authorizer for the SEFAZ TLS endpoints, reusing the catalog bundle, with TLS validation always on (checklist B) Done 2026-11-12: SP pinned to AC Raiz v10 from the ITI bundle (`SefazTlsTrust`).
- [ ] T006 Record ADR-005, ADR-007 and ADR-016 in `docs/fiscal-service/adr/`

## Phase 2 — Model and persistence

- [ ] T007 Migrations: document items, payments, protocol, SEFAZ request/response records (hashes and object references, never raw XML in the database) and an outbox table, with tenant RLS and composite keys
- [ ] T008 Enforce the closed state machine of `data-model.md` in the database (allowed transitions only; `ERROR` only from retryable stages; frozen fields after the signed boundary); add the tests in `tests/Database/`
- [ ] T009 Add the document endpoints to the 002 contract: pre-validate, issue (sync and async), get, poll example for non-terminal states, download of the sent XML, `nfeProc` and DANFE

## Phase 3 — E2 Pre-validation

- [ ] T010 Validate the request: establishment and environment, `ACTIVE` A1 within validity, CSC configured only when the QR version in use requires it, approved profile in force on the document date, items covered by rules, totals and payments consistent; typed problems for every failure; no number reserved
- [ ] T011 Map commercial payment methods to official `tPag` through the T003 table; mixed payments and change must add up exactly

## Phase 4 — Generation and signing (`Rrf5.Fiscal.Nfce`)

- [ ] T012 Access key (44 digits with the official check digit) and `cNF` from a cryptographic random source; tests with the official example values
- [ ] T013 XML generator in layout order from the normalized request and the profile snapshot; output equal to the T001 golden XML for the minimal scenario
- [ ] T014 XSD validation against the resolved package, read from the artifact store with a hash check; DTD and external entities disabled; size and depth limits; negative corpus of XXE, entity expansion and duplicate IDs (TM-09)
- [ ] T015 XMLDSig in the worker with the A1 opened from the vault (`rrf5-worker` policy), following the T002 section; verify the signature before sending; a test proves the API process cannot sign
- [ ] T016 Number allocation right before signing (`08-emission-flows.md`); a failure after the reservation leaves an audited gap

## Phase 5 — SEFAZ adapter (`Rrf5.Fiscal.Adapters.Sefaz`)

- [ ] T017 SOAP 1.2 client with mutual TLS: the client certificate comes from the A1, server trust from the T005 roots; endpoints from the catalog per authorizer and environment
- [ ] T018 Parse `retEnviNFe`, `protNFe` and `retConsSitNFe`; `AUTHORIZED` only with `cStat` 100 and the same `chNFe` and `digVal`; rejection codes and messages kept as official values
- [ ] T019 Test SOAP server (a rewrite of the POC mock): 104/100, 225, 217, timeout after authorization, unreadable answer

## Phase 6 — E1 and E3 Emission and uncertain result

- [ ] T020 Async issue: the API stores the document and the outbox message in one transaction and returns `202`; the worker consumes, signs, sends and finalizes; duplicate deliveries are harmless
- [ ] T021 Sync issue: the same path, but the API waits for a terminal state up to a configurable timeout. If the timeout expires, it answers with the non-terminal document instead of an error
- [ ] T022 Uncertain result: after a timeout or an unreadable answer, consult by access key before any resend; a consultation failure keeps `WAITING_SEFAZ` with backoff and an alert
- [ ] T023 Store the sent XML and the `nfeProc` write-once, hash-verified and with version-pinned reads (feature 004 store); the document keeps the hashes
- [ ] T024 Idempotency for issue: same key and payload return the same document; a different payload returns a conflict; no new number either way
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
