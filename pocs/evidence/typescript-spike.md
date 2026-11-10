# TypeScript spike — generation, XMLDSig and SEFAZ TLS

**Status:** DONE — functional parity with .NET and Java on every gate tested; risk profile differs  
**Executed:** 2026-11-06, Node.js 20.20.2 on the Windows development host  
**Question:** is TypeScript a viable stack for the fiscal service, given that BurgoOS (RRF5 OS) is TypeScript?

## Scope (same gates as Java)

| Gate | Implementation |
| --- | --- |
| Deterministic generation | `src/generate.ts`: string assembly with explicit escaping (Node has no standard XML writer) |
| Decimal arithmetic | `src/decimal.ts`: fixed-point on `BigInt` (JavaScript has no decimal type; `number` is binary floating point) |
| XMLDSig sign/verify | `src/dsig.ts`: `xml-crypto` 6.4.0 + own fail-closed structural checks |
| A1 (PKCS#12) | `node-forge` 1.4.3: Node cannot open a PFX to obtain a signing key (its TLS stack can use a PFX directly) |
| SEFAZ TLS | `status` command with `node:https`, PFX and optional pinned root |

New runtime dependencies (exact pins): `xml-crypto` 6.4.0 (MIT), `@xmldom/xmldom` 0.9.12 (MIT),
`node-forge` 1.4.3 (BSD-3-Clause OR GPL-2.0). `xml-crypto` brings its own `@xmldom/xmldom`
0.8.15 and `xpath` 0.0.33, so two xmldom versions coexist; the POC passes only strings to
`xml-crypto` to avoid mixing DOM implementations. Production tree: 9 packages;
`npm audit --omit=dev`: 0 vulnerabilities on 2026-11-06.

## Results

| Check | Result |
| --- | --- |
| Golden XML | SHA-256 `82e3ab6f…`, **byte-identical** to .NET and Java |
| Edge fixture (rounding, 10 decimals, escaping, UTF-8, change) | **byte-identical** (`3a045602…`) |
| Refusal parity (non-cash payment) | same code and message |
| `check-key` on 3 SEFAZ keys / altered key | valid / rejected |
| Same throwaway key: `DigestValue`, `SignatureValue`, `X509Certificate` vs .NET and Java | **identical** |
| Signed bytes vs Java | identical except `" />` vs `"/>` in empty elements written by .NET (C14N-neutral) |
| Synthetic and golden sign: verified, mutation rejected (cryptographic) | pass |
| TS verifies .NET- and Java-signed golden | pass |
| .NET verifies TS-signed golden | pass |
| TS verifies 3 Bling-signed NFC-e (issuer A1) | pass |
| TS verifies our SEFAZ-authorized `nfeProc` (`cStat` 100, `digVal` match) | pass |
| `status` with Node's bundled CAs | refused: `unable to get local issuer certificate` (also on Windows) |
| `status` with pinned ICP-Brasil v10 via `ca` | TLS accepted; SEFAZ 403 to the throwaway client cert (expected) |

Not done in the spike: authorization/consultation SOAP, container, real A1 run (the
signature parity above makes the real-A1 result predictable; it was not executed).

## Size (non-blank, non-comment lines; scopes differ)

| Candidate | Lines | Includes |
| --- | --- | --- |
| .NET | 1040 | generation 188 + signing/validation/status/verify/CLI + SOAP authorize/consult/mock 177 |
| Java | 633 | generation 201 + XMLDSig 260 + CLI/validation 172 |
| TypeScript | 553 | generation 154 + decimal 53 + XMLDSig/PFX 104 + CLI/validation/status 242 |

For the comparable features (generation + signing), the three are of similar size.
**The hypothesis that TypeScript would be materially more verbose is not supported.**

## Risk findings (where TypeScript differs)

1. **Security-critical code comes from third parties.** .NET and Java sign/verify with
   their standard libraries; TypeScript depends on `xml-crypto`, which had three
   CRITICAL signature-verification bypasses in about a year: CVE-2024-32962 (fixed
   6.0.0), CVE-2025-29774 and CVE-2025-29775 (fixed 6.0.1), per GitHub advisories
   queried on 2026-11-06. The POC adds its own structural checks (one Signature, one
   Reference to the single `infNFe` Id, embedded certificate equality) as defence in depth.
2. **PKCS#12 parsing via `node-forge`.** Eleven advisories published on 2026-10-30
   (all fixed in 1.4.1), including HIGH "PKCS#12 MAC forgery and constant-key PBE via
   zero iteration count", in the exact function used to open the A1.
3. **No decimal type.** Money needs a library or own code (53 lines here, with
   explicit half-even/half-up rules). A mistake here is a fiscal defect.
4. **Own CA bundle.** Node ignores the OS store by default; SEFAZ trust must be pinned
   explicitly on every platform (same design as the .NET container finding).
5. **Ecosystem churn.** Two DOM library versions in one tree; `@types` packages lag.

Where TypeScript is equal or better: generation and canonicalization parity, no JDK-style
policy overrides (SHA-1 accepted by `xml-crypto`), TLS with PFX natively, smallest
code, and the BurgoOS team's existing skills.

## Conclusion for the scorecard

TypeScript is **technically viable** for NFC-e/SP on the gates tested. It is no longer
eliminated on verbosity. Its distinctive cost is the security posture of the
signing and PKCS#12 dependencies, which a production decision must accept explicitly
(pinning, advisory monitoring, defence-in-depth checks, fast patch path).
