# TypeScript baseline candidate

Isolated baseline used to measure the cost of fiscal primitives in the application team's existing ecosystem.

Requirements: Node.js 20+. Run `npm install --prefix pocs/typescript`, then `npm run check --prefix pocs/typescript` and `npm start --prefix pocs/typescript`.

The `validate` command uses the pinned `xmllint-wasm` package to validate an XML
against the catalogued official XSD. It preloads all local XSD dependencies and
refuses a package whose SHA-256 differs from the frozen catalog value:

```powershell
npm run start -- validate --xml <document.xml> --xsd <schemas>/nfe_v4.00.xsd --package <schemas-010e-v1.02.zip>
```

XML generation remains blocked until a responsible fiscal stakeholder supplies
or approves the synthetic issuer and fiscal profile (CFOP, NCM, CRT and
CST/CSOSN). The POC must not infer those values from the product description.

Pending gates: approved fiscal profile and golden XML generation, XMLDSig/A1,
SOAP mock, Linux container, DANFE/QR and dependency review.

## Spike: generation, XMLDSig and SEFAZ TLS (2026-11-06)

Commands `generate`, `check-key`, `sign`, `verify` and `status` mirror the .NET and
Java POCs. Results, dependency risks and size comparison:
`pocs/evidence/typescript-spike.md`.

```powershell
npx tsx src/index.ts generate --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json --nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00 --out <path>
npx tsx src/index.ts sign --pfx <a1.pfx> [--xml <unsigned.xml> --out <signed.xml>]
npx tsx src/index.ts verify --xml <signed.xml> --cert-sha256 <A1 SHA-256>
```

