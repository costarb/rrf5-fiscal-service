# SEFAZ-SP homologation evidence

**Status:** PASS (.NET) — first NFC-e authorized by the POC on 2026-11-06; Java/TypeScript not attempted  
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

## Run 2 — real-issuer document prepared locally (PASS, not transmitted)

**Executed:** 2026-11-06 by the operator on the Windows development host
(script outside Git; password via `Read-Host -AsSecureString`, removed after).
Issuer file outside Git, series 2, `nNF` 1.

| Step | Result |
| --- | --- |
| `generate` (real issuer, output outside the repository, key not printed) | pass, 2472 bytes |
| first attempt `validate` | **invalid**: `cMunFG`/`cMun` held a CEP instead of the IBGE code; fixed in the issuer file |
| `sign` with the issuer A1 (`62cb2f73…`) | signature verified, mutation rejected |
| `validate` against 010e v1.02 | **valid** |
| `verify` (signed file SHA-256 `da39684e…`) | certificate matches A1, signature verified |

The local XSD gate caught an issuer-configuration error before any transmission.

## T018 — mock SOAP authorization (PASS, 2026-11-06)

`authorize` and `consult` share one code path; `--mock <scenario>` swaps only
the transport for canned SEFAZ-shaped answers (no network, no A1). Signed input:
synthetic golden XML signed with a throwaway PFX (deleted afterwards).

| Command / scenario | SEFAZ `cStat` | Protocol | Outcome | Exit |
| --- | --- | --- | --- | --- |
| authorize `authorized` | 104 | 100, key and `digVal` match | `AUTHORIZED`, `nfeProc` written | 0 |
| authorize `schema-rejected` | 225 | none | `NOT_AUTHORIZED` | 2 |
| authorize `timeout` | — | — | `RESULT_UNKNOWN`, instructs `consult` before anything else | 3 |
| consult `found` | 100 | 100, key and `digVal` match | `AUTHORIZED` | 0 |
| consult `not-found` | 217 | none | `NOT_FOUND` | 2 |

Additional checks:

- The `nfeProc` is built from the exact signed bytes plus the returned
  `protNFe`; `verify` on it: signature valid, `digVal` = `DigestValue`.
- `AUTHORIZED` requires protocol `cStat` 100 **and** matching `chNFe` and
  `digVal`; a 100 for a different document is not accepted.
- Refusals: unsigned document (`DOCUMENT_REFUSED`), real call without `--out`
  (`OUT_REQUIRED`), output inside the repository, `tpAmb` other than 2,
  model other than 65.
- Sanitized trace: HTTP status, elapsed time and response SHA-256 only; real
  calls save the raw answer next to `--out` (outside Git).
- Codes simulated: 104, 100, 225 (observed) and 217 (consultation "not found",
  to be confirmed against the MOC code table when the first real consultation runs).
- The POC never resends by itself. `NOT_FOUND` is reported; any resend still
  requires the explicit policy described in `pocs/shared/uncertain-result`.

## Run 3 — authorization by the POC (PASS, 2026-11-06)

**Executed:** 2026-11-06 by the operator, Windows development host, same script
and secret handling as run 2. The document was regenerated with the current
`dhEmi` (series 2, `nNF` 1, never sent before), signed, validated locally and
only then transmitted.

| Step | Result |
| --- | --- |
| `generate` | 2472 bytes, SHA-256 `8438aae8…` |
| `sign` | A1 `62cb2f73…`; verified; mutation rejected |
| `validate` (010e v1.02) | valid |
| `authorize` → `NFeAutorizacao4`, `indSinc=1` | HTTP 200 in 1655 ms |
| `retEnviNFe` | `tpAmb` 2, `verAplic` `SP_NFCE_PL_009_V400`, `cStat` **104** Lote processado |
| `protNFe/infProt` | `cStat` **100** Autorizado o uso da NF-e, `nProt` `13526000025378307` |
| Protocol `chNFe` / `digVal` match the signed document | yes / yes |
| `nfeProc` written outside the repository; raw answer saved beside it | yes |

What this proves for the .NET candidate, end to end:

- deterministic generation from fixture + profile + issuer file (T016);
- XMLDSig with the issuer A1 accepted by the authorizer (T017, .NET);
- SOAP 1.2 over mutual TLS to `NFeAutorizacao4` with sanitized trace (T018/T019);
- the frozen 010e v1.02 package plus the profile (no `IBSCBS`, CRT 1,
  CSOSN 102, PIS/COFINS CST 49) is accepted by SEFAZ-SP homologation;
- QR Code v3 online format (`p=<chave>|3|2`) **without CSC** accepted in the
  authorization. Whether the CSC is still needed for any other flow
  (contingency/offline QR) remains a T020 question.

### Run 4 — real consultation of the authorized document (PASS, 2026-11-06)

`consult --xml <signed nNF 1> --pfx <A1>` → `NFeConsultaProtocolo4`: HTTP 200 in
2130 ms, `retConsSitNFe` `cStat` 100, protocol `nProt` `13526000025378307`
(same as run 3), key and `digVal` match → `AUTHORIZED`. The consult-before-retry
path works against the real authorizer. The "not found" answer (217) is still
only simulated.

Not proven: DANFE rendering, Linux/container execution, a real "not found"
consultation, Java and TypeScript authorization,
production behaviour. The fiscal profile is still pending accountant ratification.

Numbering: series 2 `nNF` 1 is consumed. The next real test uses `nNF` 2.

## Run 5 — E4 schema probe: does SEFAZ-SP homologation apply 010g? (PENDING)

**Purpose:** close E4 with a test that tells the packages apart. NT 2026.006 and
NT 2026.008 v1.01 put 010g in homologation by 2026-10-26 and in production on
2026-11-16 (`normative-baseline.md`). The golden XML is valid in 010e, 010f and
010g alike, so re-sending it proves nothing. The probe adds the three optional
010g elements of NT 2026.008 (`vUnComLiq`, `vProdLiq` and `vProdLiqTot`).

**Probe values are placeholders.** They are copied from `vUnCom` and `vProd`
and are not a determination of the net value "sem tributos". The value rules
(I11c-10/20, W01a-10/20) start on 2027-02-01 in homologation. The option
`--schema-probe net-values-010g` exists only in the POC, is never part of a
fiscal profile, and must never be used in production.

Local pre-check (2026-11-09, synthetic issuer, throwaway key "NOT AN A1",
deleted afterwards): the probe is valid against 010g v1.03 and invalid against
010e v1.02 (`vProdLiqTot` not expected). The golden without the option is
unchanged (SHA-256 `82e3ab6f…`).

Operator procedure (PowerShell, same secret handling as runs 2 and 3; issuer
file, A1 and outputs outside Git; series 2, **`nNF` 2**, never sent before):

```powershell
cd pocs/dotnet
$env:RRF5_A1_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host -AsSecureString "A1")))
$dh  = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
$out = "<pasta fora do repositório>"
dotnet run -c Release -- generate --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer <issuer.json real> --nnf 2 --cnf <8 dígitos> --dhemi $dh --out "$out\probe.xml" --schema-probe net-values-010g
dotnet run -c Release --no-build -- sign --pfx <a1.pfx> --xml "$out\probe.xml" --out "$out\probe.signed.xml"
dotnet run -c Release --no-build -- validate --xml "$out\probe.signed.xml" --xsd <PL_010g_v1.03\NFe\nfe_v4.00.xsd> --package <PL_010g_v1.03.zip>
dotnet run -c Release --no-build -- authorize --xml "$out\probe.signed.xml" --pfx <a1.pfx> --out "$out\probe.proc.xml"
Remove-Item Env:RRF5_A1_PASSWORD
```

Transmit only if `validate` returns `valid: true`. If `authorize` returns exit 3
(`RESULT_UNKNOWN`), run `consult` on the same file before anything else and
never send `nNF` 2 again under another key.

| Outcome | Reading |
| --- | --- |
| `cStat` 100 | SEFAZ-SP homologation validates against 010g, as the national schedule requires. E4 closes for homologation; production follows the 2026-11-16 date |
| `cStat` 225 | SEFAZ-SP homologation still uses an older package, behind the NT schedule. Keep 010g-only content out, record the evidence, and recheck before 2026-11-16 |
| Any other rejection | The schema was accepted (a schema failure is 225), so 010g applies. Record the code; the probe values are not to be corrected into a fiscal rule |

Record here: date and time, `verAplic`, `cStat`/`xMotivo`, `nProt` if any, and
the signed-file SHA-256. Do not record the access key, the issuer's identifiers
or the XML.
