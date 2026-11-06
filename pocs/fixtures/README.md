# POC fixtures

Only deterministic synthetic fixtures are permitted. Validate new fixtures against `tests/FiscalCases/fiscal-case.schema.json` and never use real taxpayer or consumer data.

## `nfce-minimal.json`

Deterministic normalized input shared by all runtime candidates. It follows the
`CreateDocumentRequest` contract in
`specs/001-fiscal-service/contracts/openapi.yaml` and intentionally omits a
customer so no personal data is present.

The establishment UUID and product are synthetic. The
`FISCAL-PROFILE-APPROVAL-REQUIRED` value is a reference placeholder: it must be
resolved to an approved fiscal configuration outside the fixture. This fixture
does not assert CFOP, NCM, CST/CSOSN, tax rates or tax treatment.

All candidates must consume the values as decimal strings and must not add
runtime timestamps, random identifiers or environment-specific issuer data to
the golden XML comparison.
