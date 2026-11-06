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
