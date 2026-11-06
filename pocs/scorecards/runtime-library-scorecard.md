# Runtime and library scorecard (T029)

**Status:** SCORED — recommendation .NET 10; acceptance blocked only by gates open for all candidates (T020, T030/ADR-011)  
**Date:** 2026-11-06  
**Context decided by the operator (2026-11-06):** independent service (spec FR-001, ADR-001); language chosen here.
**Operator inputs (2026-11-06):** solo developer assisted by LLM tools; working knowledge of all three languages,
most deliveries in .NET; priorities are **performance and security**, language is secondary; no restriction on weights.
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

## 2. Weighted criteria

Weights reflect the operator's priorities (security and performance first, language
secondary). Scores 1–5 cite evidence; "pending" is not scored.

| # | Criterion | Weight | .NET 10 | Java 25 | TypeScript |
| --- | --- | --- | --- | --- | --- |
| C1 | Security of the critical chain (XMLDSig, PKCS#12, TLS) | 25 | **5** — standard library, no overrides | **4** — standard library, but secure-validation policy override and internal JDK property for Base64 | **2** — `xml-crypto` (3 CRITICAL bypass CVEs 2024–2025) and `node-forge` (11 advisories on 2026-10-30, incl. PKCS#12 MAC forgery) |
| C8 | Performance (throughput, memory, startup) — `performance-bench.md` | 15 | **5** — ~73–79 ops/s, 61 MB, no tuning | **4** — same throughput only with explicit JVM sizing; 138 MB tuned, 256 MB default | **2** — ~12 ops/s (≈6× slower; `xml-crypto` XML layer), ~143 MB |
| C2 | Proven against SEFAZ homologation | 15 | **5** — authorization + consultation, host and container | **3** — signature byte-identical to an authorized one; no SOAP | **2** — signature parity with throwaway key; TLS only |
| C3 | Maintenance complexity | 15 | **4** — 1 runtime package; no runtime tweaks | **3** — JDK policy/property overrides to re-check on upgrades; Jackson 2.x vs 3.x decision | **3** — smallest code, but own decimal arithmetic, two xmldom versions, faster dependency churn |
| C6 | Operation and portability | 10 | **5** — container proven with real A1; 204 MB image | **3** — not containerized in POC | **3** — not containerized; Node ignores OS trust store |
| C7 | Runtime support window | 10 | **4** — .NET 10 to 2028-11-14 | **5** — Temurin 25 to 2031-09-30 | **4** — Node 26 LTS to 2029-04-30 (POC ran on Node 20, EOL 2026-04-30) |
| C4 | Developer capacity (solo + LLM) | 5 | **5** — most deliveries | **4** — working knowledge | **4** — working knowledge |
| C5 | Fiscal library ecosystem | — | *pending — ADR-011* | *pending* | *pending* |

### Totals (weight 95)

| | .NET 10 | Java 25 | TypeScript |
| --- | --- | --- | --- |
| Weighted sum | 450 | 350 | 245 |
| Average (/95) | **4.74** | 3.68 | 2.58 |
| If Java/TS closed SOAP + container (C2 = 5, C6 = 4) | 4.74 | 4.11 | 3.16 |

The ranking also held in the earlier draft weighting, except in a hypothetical team with no
.NET capacity at all (near tie with TypeScript), which does not apply here. .NET leads on
the operator's two priorities (C1, C8) without depending on unfinished work. Java is a
credible second whose gap is mostly unfinished POC work plus JVM tuning and JDK
overrides. TypeScript is functionally viable but last on both priorities.

On LLM-assisted development: all three are well supported. The security argument is
stronger with LLM-generated code, because less hand-written or third-party code sits in
the signing path when the platform library does XMLDSig and PKCS#12 (.NET, Java).

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

## 4. Recommendation

**Adopt .NET 10 LTS for the fiscal service** (to be recorded in ADR-012 once the open
gates below close). Rationale: best security posture of the signing chain, best
performance and memory, the only candidate proven end to end against SEFAZ-SP
(host and Linux container), and the operator's strongest delivery record.
Plan the upgrade to the next .NET LTS before 2028-11-14.

Before ADR-012 moves to Accepted (gates open for every candidate, so they do not
change the ranking but can still block the project):

1. T020: DANFE NFC-e and QR v3 confirmation.
2. ADR-011: evaluate Unimake.DFe (and alternatives) against the self-implemented
   primitives proven here; decide library vs. own implementation per capability.
3. T030: SBOM, licences and maintenance for the .NET dependency set.
4. T011: accountant ratification of the fiscal profile (stack-independent).
