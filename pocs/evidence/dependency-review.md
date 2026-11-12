# Dependency, license, SBOM and maintenance review

**Status:** COMPLETE for discovery (T030) — remaining items are accepted
external blockers carried to `specs/001-fiscal-service/discovery-results.md` (T033)  
**Checked:** 2026-11-07 (first pass 2026-08-18)

## Current dependency inventory

| POC | Runtime dependencies | Development/build dependencies | Audit result (2026-11-07) |
| --- | --- | --- | --- |
| .NET 10 | `System.Security.Cryptography.Xml` 10.0.14 (Microsoft, MIT) for XMLDSig; transitive `System.Security.Cryptography.Pkcs` 10.0.14; `QRCoder` 1.8.1 (MIT) for the DANFE QR (T020); `System.Drawing.Common` pinned to 10.0.14 (was transitive 6.0.0, out of support) | SDK 10.0.302 | `dotnet list package --vulnerable --include-transitive`: none. Trivy on SBOM: 0 |
| Java 25 target | `jackson-databind` 2.22.4 (+ `jackson-core` 2.22.4, `jackson-annotations` 2.22) for JSON input only; XML, XSD, XMLDSig and StAX from the JDK | Maven 3.9.16, `maven-dependency-plugin` 3.11.0 | Trivy on CycloneDX SBOM: 0 |
| TypeScript XML | `xmllint-wasm` 5.2.0 | TypeScript 5.9.3, tsx 4.23.5 and type/build packages | `npm audit` (full tree): 0. Trivy on SBOM: 0 |
| Contract sample | `openapi-fetch` 0.17.0 plus helper | openapi-typescript 7.13.0, Redocly CLI 1.34.24, TypeScript 5.9.2 | runtime audit: 0; full dev audit: 3 high (all via Redocly v1 → `faker`) |
| Uncertain-result simulation | Node.js standard library only | none | no package dependency |
| .NET container (`pocs/dotnet/Dockerfile`) | `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04) + published app | `mcr.microsoft.com/dotnet/sdk:10.0` (build stage only) | Trivy image: .NET layers 0; OS packages 28 (2 high, 21 medium, 5 low; 9 with upstream fix) |

## SBOMs

CycloneDX JSON, generated on 2026-11-07 from restored/published trees, stored in
[`sbom/`](sbom/):

| File | Generator | Components |
| --- | --- | --- |
| `dotnet.cdx.json` | CycloneDX for .NET (`dotnet-CycloneDX`) | 5 NuGet packages |
| `java.cdx.json` | `org.cyclonedx:cyclonedx-maven-plugin` 2.9.1 (`makeBom`) | 3 Maven artifacts |
| `typescript.cdx.json` | `npm sbom --sbom-format cyclonedx --omit=dev` | 7 npm packages |
| `contract.cdx.json` | `npm sbom --sbom-format cyclonedx --omit=dev` | 2 npm packages |
| `container-dotnet.cdx.json` | Trivy 0.76.1 (`trivy image --format cyclonedx`) | 107 OS + .NET components |

These are discovery evidence. The release SBOM must still be emitted by the
release pipeline from the final published artifacts and attached to build
provenance; it must not be copied from this folder.

## License and maintenance observations

| Component/candidate | License evidence | Maintenance evidence | Decision at this stage |
| --- | --- | --- | --- |
| `xmllint-wasm` 5.2.0 | npm declares MIT; bundled libxml2 notices must remain in distribution review | Active package; no native runtime toolchain | Accept for isolated XSD POC only; validate bundled notices before product use |
| `openapi-fetch` 0.17.0 | MIT | Current release used by generated client; runtime audit clean | Accept for contract POC/client candidate |
| Redocly CLI 2.71.2 (was 1.34.24) | MIT; development CLI only | v2 line on Node 22; 2.71.2 published 2026-10-27 | Migrated 2026-11-11 (T037 of 002, closes L11): full `npm audit` 0 (the 3 high via v1 → `faker` are gone); lints the 001 and 002 contracts; not runtime |
| Jackson 2.22.4 | Apache-2.0 (FasterXML) | Latest 2.x on Maven Central; a 3.x line (`tools.jackson`, renamed APIs) also exists | Accept for POC JSON parsing; a Java foundation must choose 2.x vs 3.x deliberately |
| `QRCoder` 1.8.1 | MIT; `github.com/Shane32/QRCoder` | The most-used .NET QR encoder; `QRCoder.Core` is an unrelated fork and was not used | Accept for the DANFE POC. Only `QRCodeGenerator` is used, never the drawing API |
| `System.Drawing.Common` | MIT (Microsoft) | 10.0.14 is in the supported .NET 10 servicing line | Pinned explicitly so the out-of-support 6.0.0 no longer resolves. Unused by the code; the foundation should still drop it from the shipped tree when QRCoder or the chosen encoder allows |
| Unimake.DFe | GitHub declares MIT and NFe/NFCe support | Continuing activity | Candidate only; not installed |
| Java_NFe | GitHub declares MIT | v4.1.1 claims PL.010d/RTC work | Candidate only; current frozen package is 010e v1.02, so coverage must be demonstrated |

Project README claims, stars, test counts and release notes are maintenance
signals, not proof of fiscal correctness. Official schema/manual hashes and
homologation evidence prevail.

## Vulnerability disposition

**Application runtimes:** zero findings in every POC runtime tree (npm audit,
`dotnet list package --vulnerable`, Trivy against all four application SBOMs).

**Contract tooling (development only):** Redocly was raised from 1.34.19 to
1.34.24 and safe `npm audit fix` applied, taking the full dev tree from 21
findings (3 critical) to 3 high, all through `faker` inside Redocly v1. The
contract still lints clean with 1.34.24. This remains a time-bounded discovery
exception, not production acceptance:

- do not expose the CLI as a service or feed it untrusted remote specifications;
- keep `npm audit --omit=dev` as a release gate;
- move contract tooling to Node 22 and Redocly v2 before foundation release, or
  select another OpenAPI 3.1 validator with a clean maintained tree.

**Container OS layer:** all 28 findings are Ubuntu 24.04 packages of the
official `runtime:10.0` base, still present after `docker build --pull`
(base digest `sha256:4f1f30cf0bb4…`). The two high ones are
CVE-2026-109518 in `apt`/`libapt-pkg6.0t64` 2.8.3 (fixed in 2.8.4ubuntu0.1),
which the service never invokes at runtime. The chiseled variant
`mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` has no package manager
or shell and scans at **2 medium, 0 high**, at 97 MB versus 203 MB.
Recommendation for the foundation image: chiseled runtime base, rebuilt on
every base-image update, with an image scan as a release gate (fail on
fixable high/critical). The POC Dockerfile was left unchanged because the
SEFAZ-SP chain diagnostics in `container-poc.md` used shell tools.

## Reproducible commands

```text
dotnet list pocs/dotnet/Rrf5.Fiscal.Poc.csproj package --include-transitive --vulnerable
dotnet-CycloneDX pocs/dotnet/Rrf5.Fiscal.Poc.csproj --output-format Json --filename dotnet.cdx.json --output <dir>
mvn -f pocs/java/pom.xml "org.cyclonedx:cyclonedx-maven-plugin:2.9.1:makeBom" "-DoutputFormat=json" "-DoutputName=java.cdx"
npm --prefix pocs/typescript sbom --sbom-format cyclonedx --omit=dev
npm --prefix pocs/contract sbom --sbom-format cyclonedx --omit=dev
npm --prefix pocs/contract audit --omit=dev
npm --prefix pocs/contract audit
trivy sbom <file>.cdx.json
docker build --pull -t rrf5-fiscal-poc-dotnet:t030 pocs/dotnet
trivy image --format cyclonedx --output container-dotnet.cdx.json rrf5-fiscal-poc-dotnet:t030
trivy image mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
```

Note: on PowerShell 5, write SBOMs without a UTF-8 BOM (Trivy rejects them
otherwise) and quote Maven `-D` arguments containing dots.

## Foundation runtime (feature 002)

Product code under `src/` passes the same gate before a package enters
`Directory.Packages.props`.

| Package | Version | License | Maintenance evidence | Audit (2026-11-10) | Decision |
| --- | --- | --- | --- | --- | --- |
| `Npgsql` | 10.0.3 (published 2026-05-27) | PostgreSQL (`licenses.nuget.org/PostgreSQL`) | Official .NET data provider of the PostgreSQL ecosystem; 10.x line follows .NET 10 | `dotnet list package --vulnerable --include-transitive`: none. Transitive: `Microsoft.Extensions.Logging.Abstractions` 10.0.0, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0 (MIT) | Accept (ADR-014: the only runtime data dependency). GSSAPI negotiation is disabled by default because the chiseled image has no Kerberos library |
| `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore` | 1.20.1 (published 2026-10-20/21) | Apache-2.0 | CNCF OpenTelemetry .NET; 1.20.2 of the ASP.NET Core instrumentation (2026-11-06) skipped as too recent | `dotnet list package --vulnerable --include-transitive`: none. Transitive: `OpenTelemetry`, `OpenTelemetry.Api*` 1.20.1 and `Microsoft.Extensions.*` 10.0.x (MIT) | Accept (T013). Spans pass through the redaction processor before any exporter; OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.10 (same servicing line as the other ASP.NET Core packages) | MIT (Microsoft) | ASP.NET Core; pulls `Microsoft.IdentityModel.*` and `System.IdentityModel.Tokens.Jwt` 8.19.2 (MIT) | `dotnet list package --vulnerable --include-transitive`: none | Accept (ADR-013 item 4): bearer validation only; tokens are built and signed by the service through the vault |
| `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Diagnostics.Abstractions` | 10.0.10 | MIT (Microsoft) | .NET 10 servicing line, already transitive through hosting | none | Accept: lets the Application layer log and meter the expiry job without a host dependency |
| `AWSSDK.S3` (+ `AWSSDK.Core`) | 4.0.104.7 (published 2026-10-26; 4.0.104.12 of 2026-11-10 skipped as too recent) | Apache-2.0 (Amazon) | Official AWS SDK; S3 API used against SeaweedFS locally and the cloud store later | none | Accept (T035). Optional trailing checksums and aws-chunked bodies are disabled: SeaweedFS 3.97 stored the chunk framing as object content; writes are now verified by reading back |
| `OpenTelemetry.Exporter.InMemory` | 1.20.1 | Apache-2.0 | same project | none | Test-only (`tests/Security`), never shipped |

The image SBOM and Trivy scan of the foundation CI cover these packages on every push.

## Closed since the first pass

- Java 25/Maven available on this host; Java SBOM generated and scanned.
- `System.Drawing.Common` 6.0.0 no longer resolves (pinned to 10.0.14).
- Container image built, SBOM generated and OS layers scanned.
- Contract tooling raised to Redocly 1.34.24; critical findings removed.

## Accepted external blockers (carried to T033)

- No fiscal library has been added to .NET or Java; its transitive
  license/vulnerability tree can only be reviewed after the runtime decision.
  Unimake.DFe and Java_NFe must then be tested against the frozen official
  artifacts, not approved from metadata.
- Commercial/legal review must confirm redistribution obligations for
  normative artifacts and bundled native/WASM notices (libxml2 in
  `xmllint-wasm`, OS packages in the container image).
- Redocly v2 migration depends on moving contract tooling to Node 22.

This document intentionally does not select a runtime or fiscal library.

## Update 2026-11-12: XMLDSig in the product (feature 005, T015)

`System.Security.Cryptography.Xml` 10.0.14 (Microsoft, MIT) moves from the POC to the product, in `Rrf5.Fiscal.Nfce` only. It is the version reviewed above, pinned in `Directory.Packages.props`. The image SBOM and the Trivy scan in CI cover it. No other package was added. QRCoder and System.Drawing.Common stay POC-only until the DANFE (T027).
