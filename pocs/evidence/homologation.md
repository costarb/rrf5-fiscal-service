# SEFAZ-SP homologation evidence

**Status:** IN PROGRESS — T019 remains open  
**Scope:** NFC-e model 65, São Paulo, homologation only (`tpAmb=2`)

## Run 1 — service status probe (PASS)

**Executed:** 2026-10-28 16:00:18 -03:00 (SEFAZ `dhRecbto`)  
**Candidate:** .NET 10 POC, `dotnet run --project pocs/dotnet -- status --pfx <outside repository>`  
**Service:** `NFeStatusServico4` at `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeStatusServico4.asmx`

The request carried only `consStatServ` (`versao="4.00"`, `tpAmb=2`, `cUF=35`,
`xServ=STATUS`). No fiscal document, access key, CSC or taxpayer data was sent.

| Field | Value |
| --- | --- |
| HTTP status | 200 |
| Client-observed round trip | 462 ms |
| `tpAmb` | 2 |
| `cUF` | 35 |
| `verAplic` | `SP_NFCE_PL_009_V400` |
| `cStat` / `xMotivo` | 107 / Serviço em Operação |
| `tMed` | 60 |
| A1 SHA-256 fingerprint | `62cb2f73bec9596cedd38663a1e75698f2ecb29f539298e54979428d18b51798` |
| A1 `notAfter` | 2027-10-28T11:39:00-03:00 |

### Gates exercised

- A1 PKCS#12 loaded from a path outside the working tree; the command refuses
  paths inside the repository.
- Password supplied only through `RRF5_A1_PASSWORD`, entered as a secure string
  in the operator's terminal and removed afterwards; never passed as an argument
  or shown to tooling.
- Private key presence and validity window checked before transmission.
- Certificate holder: the operator confirmed on 2026-10-28 that the A1 is the
  e-CNPJ of the intended issuer (matriz). Issuer identifiers stay in
  configuration outside Git; the fingerprint above is the in-repository link.
  The probe did not compare the certificate subject programmatically.
- Mutual TLS (TLS 1.2/1.3) with the A1 client certificate completed against the
  homologation host using the operating system trust store; server certificate
  validation was not disabled.
- SOAP 1.2 envelope accepted and `retConsStatServ` parsed with DTD processing
  prohibited.
- Output limited to fingerprint, validity and SEFAZ status fields; certificate
  subject, password and raw bodies were not printed.

### Not proven by this run

- XML generation, XMLDSig signing or authorization (`NFeAutorizacao4`).
- QR Code/CSC handling or DANFE.
- Linux/container execution; this run used the Windows development host.
- Java and TypeScript candidates.

### Open observation

`verAplic` reports `SP_NFCE_PL_009_V400`, while the frozen schema package in
`normative-baseline.md` is 010e v1.02. `verAplic` is the authorizer's
application identifier and does not by itself state which schema package the
service validates against. Confirm through the official SEFAZ-SP/NF-e portal
or a homologation authorization response before pinning golden XML behavior;
do not infer schema acceptance from this string.

## Reference documents issued by a third-party ERP (Bling)

**Recorded:** 2026-11-06. The operator emitted NFC-e in SEFAZ-SP homologation
through Bling with the **same A1** (embedded certificate SHA-256 equals the
fingerprint above). Files stay outside Git because they carry issuer
identifiers and the certificate's personal SAN data; only file hashes are kept.

| File SHA-256 (prefix) | Emitted | `CRT` | Protocol | `cStat` |
| --- | --- | --- | --- | --- |
| `1dcfe783…` | 2026-10-30 | 4 | present | 100 |
| `a2485370…` | 2026-10-30 | 4 | present | 100 |
| `eda4a927…` | 2026-11-06 | 1 | absent — **rejected** | 225 |

These are not our POC's emissions and do not close T019. They are a structural
reference that SEFAZ-SP accepted:

- **QR Code version 3 without CSC hash** for normal online emission:
  `qrCode = <url>?p=<chave>|3|<tpAmb>`. If confirmed in the frozen DANFE/QR
  manual or NT, the CSC is not needed for online authorization, and T019/T020
  dependencies change. Contingency QR format not observed.
- **`IBSCBS` per item and `IBSCBSTot`** accepted while `verAplic` still reads
  `SP_NFCE_PL_009_V400`, so the authorizer accepts the RTC groups. Whether a
  CRT 1 issuer must send them is an open item in `fiscal-profile.md`.
- Homologation requires the first item's `xProd` to be
  `NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL`.
- `infRespTec` present (software house identification); production will need
  RRF5's own technical-responsible data.
- `tpImp=4`, `modFrete=9`, no `dest`, card payment with `card/tpIntegra=2`.
- Signature: see `xmldsig-poc.md`, "Cross-check against authorized documents".

### Schema drift observed: rejection 225 on 2026-11-06

The third document was rejected with `225 - Rejeição: Falha no Schema XML do
lote de NFe` (reported by the operator from Bling). Operator analysis: compared
with the two documents authorized on 2026-10-30, it newly contains
`vUnComLiq`, `vProdLiq` (item) and `vProdLiqTot` (total). The authorizer still
reported `verAplic` `SP_NFCE_PL_009_V400` at that time.

Consequences for the service design (not a tax interpretation):

- The schema package an authorizer validates against can lag behind what an
  emitter adopts. Golden XML must target the package **the authorizer accepts
  on the emission date**, not the newest published one.
- Checked 2026-11-06: these elements do **not** exist in the frozen 010e v1.02
  package, and all three POC validators reject this document locally with the
  same cause (`xml-xsd-poc.md`). Which NT introduces them, and its
  homologation/production dates, is still to be identified from official
  sources; do not infer from Bling.
- Schema version must be a per-authorizer, per-environment, dated
  configuration (ADR-009), and a 225 in homologation is a release blocker, not
  something to retry.
- This document remains signature evidence only (see `xmldsig-poc.md`).

## Remaining for T019

Authorization of one sanitized NFC-e in homologation requires the approved
synthetic fiscal profile (T016), XMLDSig with the A1 (T017), the SOAP mock and
uncertain-result handling (T018) and a homologation CSC supplied outside the
repository.
