# XMLDSig/A1 POC evidence

**Status:** IN PROGRESS — .NET and Java 25 pass (both match a SEFAZ-authorized signature); TypeScript spike pending; MOC algorithm reference pending  
**Scope:** enveloped signature over a synthetic, non-fiscal `infNFe`; nothing transmitted

## .NET 10 — PASS with the issuer A1

**Executed:** 2026-10-28, Windows development host  
**Command:** `dotnet run --project pocs/dotnet -- sign --pfx <outside repository>`  
**Library:** `System.Security.Cryptography.Xml` 10.0.14 (Microsoft, MIT)

| Check | Result |
| --- | --- |
| A1 loaded from outside the working tree, password via `RRF5_A1_PASSWORD` | Pass |
| A1 SHA-256 fingerprint | `62cb2f73bec9596cedd38663a1e75698f2ecb29f539298e54979428d18b51798` (same as `homologation.md` run 1) |
| Canonicalization | `http://www.w3.org/TR/2001/REC-xml-c14n-20010315` |
| Signature method | `http://www.w3.org/2000/09/xmldsig#rsa-sha1` |
| Digest method | `http://www.w3.org/2000/09/xmldsig#sha1` |
| Transforms | enveloped-signature, C14N |
| Reference | `#NFe00000000000000000000000000000000000000000000` (synthetic Id) |
| KeyInfo | `X509Data` with the A1 certificate |
| Signature verified | Pass |
| Value changed after signing (`10.00` -> `10.01`) rejected | Pass |

The verifier fails closed unless the document has exactly one `Signature`, one
`Reference` whose URI targets the single `infNFe` carrying the expected `Id`, and
an embedded certificate byte-identical to the loaded A1. This guards against
signature wrapping and re-targeted references in addition to content mutation.

Before the real-A1 run, the same command was exercised with a throwaway
self-signed PKCS#12 created in the session scratchpad (deleted afterwards):
signature verified, mutation rejected, wrong password rejected
(`A1_LOAD_FAILED`) and output inside the repository refused
(`OUTPUT_INSIDE_REPOSITORY`).

## Cross-check against authorized documents — PASS

**Executed:** 2026-11-06, Windows development host  
**Command:** `dotnet run --project pocs/dotnet -- verify --xml <outside repository> --cert-sha256 62cb2f73…51798`

The `verify` command checks documents emitted through Bling with the same A1
(see `homologation.md`). It needs only the embedded public certificate, uses
the same fail-closed `VerifyNfeSignature` as `sign`, and prints no access key,
issuer identifier or item data.

| File SHA-256 (prefix) | Cert matches A1 | Signature verified | `vNF` +0.01 rejected | `digVal` = `DigestValue` | `cStat` |
| --- | --- | --- | --- | --- | --- |
| `1dcfe783…` | Pass | Pass | Pass | Pass | 100 |
| `a2485370…` | Pass | Pass | Pass | Pass | 100 |
| `eda4a927…` | Pass | Pass | Pass | n/a (rejected 225, no protocol) | — |

Negative control: a wrong `--cert-sha256` returns `certificateSha256Matches=false`
and exit code 2.

All three use C14N, RSA-SHA1, SHA-1 digest and enveloped-signature + C14N
transforms with the `#NFe<chave>` reference, the same profile the POC produces.
Two of them carry an authorization protocol whose `digVal` equals the signed
digest. This is practical evidence that SEFAZ-SP accepts this profile for this A1.
It does not replace the MOC confirmation below.

## Java 25 — PASS with a throwaway key (2026-11-06)

**Runtime:** Temurin 25.0.5+8 LTS, Maven 3.9.16. **Library:** JDK built-in
`javax.xml.crypto.dsig` (JSR 105); no third-party dependency.
**Commands:** `sign --pfx <pfx> [--xml <unsigned> --out <path>]`, `verify --xml <path> --cert-sha256 <fp>`.

### Finding 1: JDK 25 secure validation forbids the NF-e algorithms

With default settings, verification fails:
`MarshalException: It is forbidden to use algorithm http://www.w3.org/2000/09/xmldsig#rsa-sha1 when secure validation is enabled`.
The JDK's `jdk.xml.dsig.secureValidationPolicy` disallows `xmldsig#sha1` and
`xmldsig#rsa-sha1`, both mandated by the NF-e signature profile.

Resolution in the POC: at start-up, remove **only** those two entries from the
policy and keep the other 15 rules (transform/reference limits, no `file`/`http`/
`https` reference URIs, minimum key sizes, no duplicate Ids, no RetrievalMethod
loops). Secure validation is not disabled. A JDK upgrade that changes the policy
format or removes SHA-1 support entirely is a recurring maintenance risk for a
Java stack.

### Finding 2: JDK wraps Base64 in the signature

By default the JDK writes `SignatureValue` and `X509Certificate` wrapped every 76
characters with `&#13;` + line feed (17 occurrences in the golden). SEFAZ-authorized
documents carry unwrapped values. Setting
`com.sun.org.apache.xml.internal.security.ignoreLineBreaks=true` (an internal
JDK property) removes the wrapping; `standalone="no"` was also removed from the
declaration.

### Results

A throwaway self-signed PFX (deleted afterwards) was used for signing in both runtimes.

| Check | Result |
| --- | --- |
| Synthetic payload: sign, verify, mutation rejected (cryptographic) | pass |
| Golden NFC-e: sign, verify, `vNF` mutation rejected (cryptographic) | pass |
| Same key, same golden: Java vs .NET `DigestValue` / `SignatureValue` | **identical** |
| Signed bytes Java vs .NET | identical except attribute order in `infNFe` (Java DOM writes `Id` before `versao`); C14N output is the same |
| Java verifies .NET-signed golden | pass |
| .NET verifies Java-signed golden | pass |
| Java verifies the three Bling-signed NFC-e (issuer A1) | pass, pass, pass |
| Java verifies our SEFAZ-authorized `nfeProc` (series 2, nNF 1) | pass; `digVal` matches, `cStat` 100 |
| Wrong `--cert-sha256` | `certificateSha256Matches=false` |
| Mutation test counts only a cryptographic failure (not an exception) | yes |

Because RSA PKCS#1 v1.5 is deterministic, identical `SignatureValue` across
runtimes shows the Java canonicalization equals the .NET one byte for byte.

### Java with the issuer A1 — PASS (operator run, 2026-11-06)

Java re-signed the unsigned series 2 `nNF` 1 document (the one authorized in
`homologation.md` run 3) with the issuer A1; nothing was transmitted.

| Check | Result |
| --- | --- |
| A1 fingerprint | `62cb2f73…` |
| `sign`: verified / mutation rejected | pass / pass |
| `verify` (file SHA-256 `be856cf1…`): certificate matches / signature verified | pass / pass |
| `DigestValue` identical to the SEFAZ-authorized document | **true** |
| `SignatureValue` identical to the SEFAZ-authorized document | **true** |

The Java signature is therefore byte-identical to one SEFAZ-SP homologation
authorized (`cStat` 100), without spending a new number.

## Not proven

- SEFAZ acceptance of a signature produced by **our** code; only an
  authorization in homologation (T019) proves it. The Bling cross-check above
  proves the algorithm profile and the A1, not our serializer.
- Signature over a schema-valid NFC-e; the payload is synthetic until T016.
- Algorithm choice against the frozen MOC 7.0 signature chapter: RSA-SHA1/SHA-1
  with C14N follows the established NF-e convention but must be confirmed in the
  frozen MOC bytes before T017 is closed.
- Linux/container execution and certificate handling in a secret store.

## Remaining for T017

- TypeScript candidate: same payload, same checks.
- Confirm algorithms against MOC 7.0 and record the section reference here.
