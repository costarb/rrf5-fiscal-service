# .NET 10 candidate

Isolated discovery scaffold. It must not be referenced by the future production solution.

Requirements: .NET SDK 10. Run `dotnet run --project pocs/dotnet/Rrf5.Fiscal.Poc.csproj`.

The `validate` command validates an XML against the catalogued official XSD and
refuses a package whose SHA-256 differs from the frozen catalog value:

```powershell
dotnet run -- validate --xml <document.xml> --xsd <schemas>/nfe_v4.00.xsd --package <schemas-010e-v1.02.zip>
```

The `status` command performs a read-only `NFeStatusServico4` probe against
SEFAZ-SP homologation using an A1 stored outside the repository. The password is
read only from `RRF5_A1_PASSWORD`. Evidence: `pocs/evidence/homologation.md`.

```powershell
dotnet run -- status --pfx <path outside the repository>\a1.pfx
```

The `sign` command (T017) signs a synthetic, non-fiscal `infNFe` with the A1
(enveloped XMLDSig, C14N, RSA-SHA1/SHA-1 digest), verifies it, and checks that a
post-signature value change is rejected. Verification fails closed unless there
is exactly one signature, one reference to the single `infNFe` with the expected
`Id`, and the embedded certificate equals the A1. Nothing is transmitted.

```powershell
dotnet run -- sign --pfx <path outside the repository>\a1.pfx [--out <path outside the repository>\signed.xml]
```

The `verify` command checks the XMLDSig of an already-authorized document
(for example, one issued through another ERP with the same A1). It needs only
the embedded public certificate, requires the expected A1 fingerprint, adds a
`vNF` mutation-negative check and compares `protNFe/digVal` with the signed
digest. The XML must live outside the repository; output carries no access key
or issuer data.

```powershell
dotnet run -- verify --xml <path outside the repository>\nfce.xml --cert-sha256 <A1 SHA-256>
```

The `generate` command (T016) builds an unsigned NFC-e from the normalized
fixture, a fiscal profile file and an issuer file. `dhEmi`, `cNF` and `nNF` are
explicit so output is deterministic. A real issuer requires `--out` outside the
repository. `check-key` validates an access key check digit.

```powershell
dotnet run -- generate --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json --nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00 --out <path>
dotnet run -- sign --pfx <outside repository>\a1.pfx --xml <unsigned.xml> --out <outside repository>\signed.xml
```

`authorize` sends one signed NFC-e to SEFAZ-SP homologation
(`NFeAutorizacao4`, synchronous) and writes the `nfeProc` only when protocol
`cStat` is 100 with matching key and digest. `consult` queries
`NFeConsultaProtocolo4` for the same signed document. Any transport failure is
`RESULT_UNKNOWN` (exit 3): run `consult` before anything else; the POC never
resends. `--mock` replaces the transport with canned answers.

```powershell
dotnet run -- authorize --xml <signed.xml> --pfx <a1.pfx> --out <outside repository>\proc.xml
dotnet run -- consult --xml <signed.xml> --pfx <a1.pfx> [--out <outside repository>\proc.xml]
dotnet run -- authorize --xml <signed.xml> --mock authorized|schema-rejected|timeout
dotnet run -- consult --xml <signed.xml> --mock found|not-found
```

### Linux container

```powershell
docker build -t rrf5-fiscal-poc-dotnet:discovery pocs/dotnet
docker run --rm -v <private dir>:/work:ro -v <repo>\sefaz\trust:/trust:ro `
  -e RRF5_A1_PASSWORD -e RRF5_SEFAZ_TRUST_ROOT=/trust/icp-brasil-v10.crt `
  rrf5-fiscal-poc-dotnet:discovery status --pfx /work/a1.pfx
```

Linux images do not trust the ICP-Brasil root that anchors SEFAZ-SP;
`RRF5_SEFAZ_TRUST_ROOT` pins it (SHA-256 checked in code). Evidence:
`pocs/evidence/container-poc.md`.

Pending gates: Linux container run with the real A1,
DANFE/QR and dependency review.
