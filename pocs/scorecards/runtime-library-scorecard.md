# Runtime and library scorecard (T029)

**Status:** DRAFT — technical scores filled from evidence; team-capacity input and three open gates pending  
**Date:** 2026-11-06  
**Context decided by the operator (2026-11-06):** independent service (spec FR-001, ADR-001); language chosen here.
**Candidates:** .NET 10 LTS, Java 25 LTS (Temurin), TypeScript on Node.js LTS.

## 1. Eliminatory gates (plan.md "POC Acceptance Gates")

| Gate | .NET 10 | Java 25 | TypeScript | Evidence |
| --- | --- | --- | --- | --- |
| XML: deterministic, XSD-valid | PASS — golden; XSD valid; SEFAZ authorized | PASS — byte-identical golden; XSD valid | PASS — byte-identical golden; XSD valid | `xml-xsd-poc.md`, `typescript-spike.md` |
| Signature: verified, mutation rejected | PASS — SEFAZ authorized our signature | PASS — real-A1 signature identical to the authorized one | PASS (throwaway key) — identical to .NET/Java with same key | `xmldsig-poc.md`, `typescript-spike.md` |
| A1: external secret, fingerprint, validity, no leak | PASS (real A1) | PASS (real A1) | PARTIAL — throwaway only; PKCS#12 via `node-forge` | `homologation.md`, `xmldsig-poc.md` |
| SOAP: mock and homologation | PASS — mock + authorization 104/100 + consultation | NOT DONE | PARTIAL — TLS to SEFAZ only (status 403 with throwaway) | `homologation.md`, `typescript-spike.md` |
| Uncertain result: consult before retry | PASS — mock timeout → `RESULT_UNKNOWN`; real consultation | NOT DONE in Java (shared Node simulation exists) | NOT DONE in the spike (shared simulation is Node) | `homologation.md`, `pocs/shared/uncertain-result` |
| DANFE/QR | **OPEN (T020)** | **OPEN (T020)** | **OPEN (T020)** | QR v3 online accepted by SEFAZ (`homologation.md`); DANFE not started |
| Portability: Linux container | PASS — Ubuntu 24.04, non-root, real A1 status 107 | NOT DONE | NOT DONE | `container-poc.md` |
| License: compatible, SBOM | PARTIAL — MIT (Microsoft); no SBOM yet | PARTIAL — Apache-2.0 (Jackson); no SBOM | PARTIAL — MIT/BSD-3; no SBOM | `dependency-review.md` (T030 open) |
| Maintenance: activity, response to NTs | PARTIAL — stack only; fiscal library not evaluated | PARTIAL — idem | PARTIAL — idem | ADR-011 open |

No candidate has failed a gate. DANFE/QR, SBOM and the fiscal-library question are open
for all three, so **no final decision can be accepted yet** under the plan's rules.

## 2. Weighted criteria (weights are a proposal for the operator to confirm)

Scores 1–5. Each score cites evidence; "pending" is not scored.

| # | Criterion | Weight | .NET 10 | Java 25 | TypeScript |
| --- | --- | --- | --- | --- | --- |
| C1 | Security of the critical chain (XMLDSig, PKCS#12, TLS) | 20 | **5** — standard library, no overrides | **4** — standard library, but secure-validation policy override and internal JDK property for Base64 | **2** — `xml-crypto` (3 CRITICAL bypass CVEs 2024–2025) and `node-forge` (11 advisories on 2026-10-30, incl. PKCS#12 MAC forgery) |
| C2 | Proven against SEFAZ homologation | 15 | **5** — authorization + consultation, host and container | **3** — signature byte-identical to an authorized one; no SOAP | **2** — signature parity with throwaway key; TLS only |
| C3 | Maintenance complexity | 15 | **4** — 1 runtime package; no runtime tweaks | **3** — JDK policy/property overrides to re-check on upgrades; Jackson 2.x vs 3.x decision | **3** — smallest code, but own decimal arithmetic, two xmldom versions, faster dependency churn |
| C4 | Team capacity (build and operate) | 20 | *input pending* | *input pending* | *input pending* |
| C5 | Fiscal library ecosystem (DANFE, events, NT updates) | 10 | *pending — ADR-011* | *pending — ADR-011* | *pending — ADR-011* |
| C6 | Operation and portability | 10 | **5** — container proven with real A1; 204 MB image | **3** — not containerized in POC | **3** — not containerized; Node ignores OS trust store |
| C7 | Runtime support window | 10 | **4** — .NET 10 to 2028-11-14 | **5** — Temurin 25 to 2031-09-30 | **4** — Node 26 LTS to 2029-04-30 (POC ran on Node 20, EOL 2026-04-30) |

### Partial totals (C1, C2, C3, C6, C7 — weight 70)

| | .NET 10 | Java 25 | TypeScript |
| --- | --- | --- | --- |
| Weighted sum | 325 | 250 | 185 |
| Average (/70) | **4.64** | 3.57 | 2.64 |

C2 and C6 for Java and TypeScript reflect **work not done**, not proven inability. If
both finished SOAP, homologation and container (C2 = 5, C6 = 4): Java 4.14, TypeScript 3.43.
.NET stays ahead because of C1 and C3.

### Sensitivity to team capacity (C4, weight 20 → total weight 90)

| Scenario (C4 scores .NET / Java / TS) | .NET | Java | TypeScript |
| --- | --- | --- | --- |
| Team equally able in all (3/3/3) | 4.28 | 3.44 | 2.72 |
| TypeScript-only team, can learn (2/2/5) | 4.06 | 3.22 | 3.17 |
| TypeScript-only team, no .NET capacity (1/1/5) | 3.83 | 3.00 | 3.17 |
| Same, and TS completes SOAP/container (1/1/5, TS C2=5, C6=4) | 3.83 | 3.00 | **3.78** |

TypeScript reaches parity only when the team has essentially no .NET/Java capacity
**and** TypeScript closes the SOAP/container gaps. Even then the C1 dependency risk remains
and must be accepted explicitly.

## 3. Fiscal library question (ADR-011) — not yet evaluated

The POCs implemented the primitives (generation, XMLDSig, SOAP) on standard libraries.
ADR-011 and research.md ask whether to adopt a maintained fiscal library instead
(Unimake.DFe for .NET; Java_NFe for Java; none identified for TypeScript). Still open:

- does a library cover the frozen 010e v1.02 package, NT 2025.002 (RTC) and SEFAZ-SP NFC-e,
  DANFE NFC-e and QR v3?
- licence, release cadence, response time to NTs, test suite, replaceability behind our interfaces.

Evidence from this phase lowers the risk of the self-implemented path for the primitives
proven here; DANFE, events (cancellation, inutilização) and contingency remain the
larger surface where a library may matter most.

## 4. Provisional recommendation

**.NET 10 is the provisional front-runner** on every scored criterion except C7, with the
most evidence (SEFAZ authorization, container with real A1, no runtime overrides, no
third-party crypto). Java 25 is a sound second; TypeScript is viable but carries the
highest security-dependency risk.

Before ADR-012 can be accepted:

1. Operator: confirm weights and provide C4 (team capacity) — the only input that can
   change the ranking.
2. T020: DANFE NFC-e and QR v3 confirmation (gate open for all).
3. T030: SBOM/licence/maintenance for the chosen candidate; ADR-011 library evaluation
   (at least Unimake.DFe for .NET).
4. T011: accountant ratification of the fiscal profile (independent of stack).
