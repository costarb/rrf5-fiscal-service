# XMLDSig/A1 POC evidence

**Status:** IN PROGRESS — T017 remains open (Java and TypeScript pending)  
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

- Java 25 and TypeScript candidates: same synthetic payload, same checks.
- Confirm algorithms against MOC 7.0 and record the section reference here.
