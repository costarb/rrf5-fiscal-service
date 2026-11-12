# POC fixtures

Only deterministic synthetic fixtures are permitted. Validate new fixtures against `tests/FiscalCases/fiscal-case.schema.json` and never use real taxpayer or consumer data.

## `nfce-minimal.json`

Deterministic normalized input shared by all runtime candidates. It follows the
`CreateDocumentRequest` contract in
`specs/001-fiscal-service/contracts/openapi.yaml` and intentionally omits a
customer so no personal data is present.

The establishment UUID and product are synthetic. Its `fiscalProfileId`
references `SP-SN-OWN-PRODUCTION-V1`; the fixture itself does not assert CFOP,
NCM, CST/CSOSN, tax rates or tax treatment.

All candidates must consume the values as decimal strings and must not add
runtime timestamps, random identifiers or environment-specific issuer data to
the golden XML comparison.

## `fiscal-profile-sp-sn-own-production-v1.json`

Machine-readable form of the interim operator-approved profile recorded in
`pocs/evidence/fiscal-profile.md` (accountant ratification pending). It is the
only source of fiscal values for the golden XML.

## `issuer-synthetic.json`

Synthetic issuer (all-zero CNPJ/IE) for deterministic golden output. It is never
transmitted. The real issuer file has the same shape and lives outside Git.

## `golden/nfce-minimal-sp-sn-v1.unsigned.xml`

Unsigned golden NFC-e produced by the .NET POC from the three files above with
`--nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00`. Other candidates
must reproduce it byte for byte. Hash and checks: `pocs/evidence/xml-xsd-poc.md`.

## Note on the golden cNF (2026-11-12)

The golden uses `cNF` 12345678. MOC 7.0 Anexo I rule B09-20 (rejection 897, NT 2019.001) forbids that value, so a real authorizer would reject this document. The golden is synthetic and never transmitted; it pins generator output byte for byte. The product draws `cNF` from a cryptographic source that satisfies B09-20 (`AccessKey.NewNumericCode`).
