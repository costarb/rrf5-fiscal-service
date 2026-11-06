# Dependency, license, SBOM and maintenance review

**Status:** IN PROGRESS — T030 remains open  
**Checked:** 2026-08-18

## Current dependency inventory

| POC | Runtime dependencies | Development/build dependencies | Audit result |
| --- | --- | --- | --- |
| .NET 10 | `System.Security.Cryptography.Xml` 10.0.14 (Microsoft, MIT) for XMLDSig; transitive `System.Security.Cryptography.Pkcs` 10.0.14 | SDK 10.0.302 | `dotnet list package --include-transitive` on 2026-10-28: those two packages only; `dotnet list package --vulnerable`: none |
| Java 25 target | JDK APIs only; no Maven dependency | Maven compiler configuration only | Maven/Java 25 execution unavailable on this host |
| TypeScript XML | `xmllint-wasm` 5.2.0 | TypeScript 5.9.3, tsx 4.23.5 and type/build packages | npm audit previously passed with zero findings |
| Contract sample | `openapi-fetch` 0.17.0 plus helper | openapi-typescript 7.13.0, Redocly CLI 1.34.19, TypeScript 5.9.2 | runtime audit: zero; full dev audit: 12 moderate, 2 high |
| Uncertain-result simulation | Node.js standard library only | none | no package dependency |

NPM lockfiles are the reproducible package inventories for the two npm POCs.
The Java POM is an empty runtime manifest; the .NET project file pins only the
Microsoft XMLDSig package. A release SBOM
must be emitted from the final restored/published artifacts, not reconstructed
from this table.

## License and maintenance observations

| Component/candidate | License evidence | Maintenance evidence | Decision at this stage |
| --- | --- | --- | --- |
| `xmllint-wasm` 5.2.0 | npm declares MIT; bundled libxml2 notices must remain in distribution review | Published approximately four months before this check; active package and no native runtime toolchain | Accept for isolated XSD POC only; validate bundled notices/SBOM before product use |
| `openapi-fetch` 0.17.0 | npm/GitHub project uses MIT ecosystem licensing | Current release used by generated client; runtime audit clean | Accept for contract POC/client candidate |
| Redocly CLI 1.34.19 | development CLI; license metadata retained in lock tree | Maintained v2 exists, but requires Node 22 | Restricted to trusted local contract lint in CI; not runtime-approved; migrate to v2/Node 22 |
| Unimake.DFe | GitHub repository declares MIT and NFe/NFCe support | Repository shows continuing activity and extensive history | Candidate only; not installed or approved until T016–T020 prove current XSD, XMLDSig/A1, SOAP, RTC, DANFE/QR and SP behavior |
| Java_NFe | GitHub declares MIT; Maven coordinates documented by project | v4.1.1 and recent history claim PL.010d/RTC work and tests | Candidate only; current official package frozen here is 010e v1.02, so coverage must be demonstrated rather than inferred |

Project README claims, stars, test counts and release notes are maintenance
signals, not proof of fiscal correctness. Official schema/manual hashes and
homologation evidence prevail.

## Vulnerability disposition

`openapi-fetch`, the only runtime dependency of the sample client, has zero npm
audit findings. Redocly v1.34.19 brings 14 transitive development findings (12
moderate, 2 high) through its telemetry/HTTP tooling. It runs only in CI against
the repository-owned OpenAPI file and is neither imported nor shipped.

This is a time-bounded discovery exception, not production acceptance:

- do not expose the CLI as a service or feed it untrusted remote specifications;
- keep `npm audit --omit=dev` as a release gate;
- run full audit visibly and track the development exception;
- move contract tooling to Node 22 and Redocly v2 before foundation release, or
  select another OpenAPI 3.1 validator with a clean maintained tree.

Testing Redocly 1.27.2 did not solve the issue: it retained two high findings,
including prototype pollution in `redoc`, so the tested 1.34.19 pin was restored.

## Reproducible inventory commands

```text
dotnet list pocs/dotnet/Rrf5.Fiscal.Poc.csproj package --include-transitive
npm --prefix pocs/typescript ls --all --json
npm --prefix pocs/typescript audit
npm --prefix pocs/contract ls --all --json
npm --prefix pocs/contract audit --omit=dev
npm --prefix pocs/contract audit
```

For final delivery, generate CycloneDX JSON for every published application and
container, preserve package hashes/licenses, and attach it to build provenance.
An npm lockfile alone is not the final multi-runtime SBOM.

## Remaining T030 blockers

- no fiscal library has been added to .NET or Java, so its complete transitive
  license/vulnerability tree cannot yet be reviewed;
- Java 25/Maven are unavailable on this host;
- DANFE/QR dependencies are not selected;
- no container image exists for OS-package SBOM and layer scanning;
- Unimake.DFe and Java_NFe must be tested against the frozen official artifacts,
  not approved from metadata;
- commercial/legal review must confirm redistribution obligations for normative
  artifacts and bundled native/WASM notices.

T030 may be checked only after these blockers are closed or recorded as accepted
external blockers in discovery results. This document intentionally does not
select a runtime or fiscal library.
