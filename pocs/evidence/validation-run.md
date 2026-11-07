# Full discovery validation run (T035)

**Status:** PASS except the gitleaks history scan, which is pending (see "Open")  
**Executed:** 2026-11-07, Windows 11 host + Docker Desktop (Linux engine 29.9.2)  
**Inputs:** synthetic fixtures only; throwaway self-signed key marked "NOT AN A1",
created per run outside the repository and deleted after use. No real A1 or CSC.

## Results

| Area | Check | Result |
| --- | --- | --- |
| Repository | JSON parse of every tracked JSON + unique task IDs (CI step) | PASS — 37 tasks |
| Repository | `python scripts/verify_secret_boundaries.py` | PASS |
| Repository | gitleaks full-history scan | **OPEN** — not installed locally; CI not running |
| .NET 10 | `dotnet build -c Release` | PASS — 0 warnings, 0 errors |
| .NET 10 | `generate` golden | PASS — SHA-256 `82e3ab6f83b85555f551d597afad93cba6a9d6557ad03b4ac34e77b255a504b1` |
| .NET 10 | `sign` (throwaway key) | PASS — `signatureVerified: true`, `mutationRejected: true` |
| .NET 10 | `validate` against 010e v1.02 ZIP (hash-pinned) | PASS |
| .NET 10 | `danfe-selftest` | PASS — 36/36 |
| .NET 10 | `authorize --mock authorized / schema-rejected / timeout` | PASS — exit 0 / 2 / 3; timeout returns `RESULT_UNKNOWN` and directs to `consult` |
| .NET 10 | `consult --mock found / not-found` | PASS — exit 0 / 2 |
| .NET 10 | Canary password in stdout/stderr of all commands above | PASS — absent |
| Java 25 | `mvn package` | PASS |
| Java 25 | `generate` golden | PASS — byte-identical to .NET |
| Java 25 | `validate` the .NET-signed document (010e ZIP) | PASS |
| TypeScript | `tsc --noEmit`, `npm audit` | PASS — 0 vulnerabilities |
| TypeScript | `generate` golden | PASS — byte-identical to .NET |
| TypeScript | `validate` the .NET-signed document (010e ZIP) | PASS |
| Uncertain result | `node --test` | PASS — 4/4 |
| Contract | OpenAPI YAML safe-load | PASS |
| Contract | `redocly lint --extends=spec` (1.34.24) | PASS — valid |
| Contract | Regenerated client equals committed `src/generated/schema.d.ts` | PASS |
| Contract | `tsc --noEmit` | PASS |
| Contract | `npm audit --omit=dev`; `npm audit --audit-level=critical` | PASS — 0 runtime; no critical |
| Container | `docker build` (`pocs/dotnet/Dockerfile`) | PASS |
| Container | `danfe-selftest` inside the Linux container | PASS — 36/36 (closes the run part of L3 on the Ubuntu base) |
| Container | Canary `.pfx`, `.env`, `.key` planted in the build context | PASS — absent from exported filesystem and image history; 0 key/env files in the image |
| Container | Runtime user | PASS — non-root (UID 1654) |
| Container | Trivy OS scan (from T030) | 2 high in `apt` with upstream fix on `runtime:10.0`; chiseled base recommended (ADR-012) |

Additional schema diagnostic (2026-11-07): the signed golden is also valid against
010f v1.04 and 010g v1.03 (`normative-baseline.md`).

## Commands

Paths in angle brackets are outside the repository.

```text
python scripts/verify_secret_boundaries.py
cd pocs/dotnet
dotnet build -c Release
dotnet run -c Release --no-build -- generate --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json --nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00 --out <golden.xml>
dotnet run -c Release --no-build -- sign --pfx <throwaway.pfx> --xml <golden.xml> --out <signed.xml>   # password in RRF5_A1_PASSWORD
dotnet run -c Release --no-build -- validate --xml <signed.xml> --xsd <PL_010e_v1.02/NFe/nfe_v4.00.xsd> --package <PL_010e_v1.02.zip>
dotnet run -c Release --no-build -- danfe-selftest --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json
dotnet run -c Release --no-build -- authorize --xml <signed.xml> --mock authorized|schema-rejected|timeout
dotnet run -c Release --no-build -- consult --xml <signed.xml> --mock found|not-found
cd pocs/java && mvn package && java -cp "target/fiscal-java-poc-0.1.0-SNAPSHOT.jar;target/lib/*" com.rrf5.fiscal.poc.Main generate|validate ...
cd pocs/typescript && npm run check && npx tsx src/index.ts generate|validate ...
cd pocs/shared/uncertain-result && npm test
cd pocs/contract && npm ci && npm run check && npm audit --omit=dev && npm audit --audit-level=critical
docker build -t rrf5-fiscal-poc-dotnet:t035 <context with planted canaries>
docker run --rm -v <fixtures>:/fx:ro rrf5-fiscal-poc-dotnet:t035 danfe-selftest --case /fx/nfce-minimal.json --profile /fx/fiscal-profile-sp-sn-own-production-v1.json --issuer /fx/issuer-synthetic.json
docker export <container> | search for canary; docker history --no-trunc
```

Host notes: on PowerShell, call `npx tsx src/index.ts` directly (the `npm.ps1` shim
mangles `npm run start -- --flag` arguments). `pocs/contract` was run from a clean
copy outside OneDrive because OneDrive locks a stale temp folder in its
`node_modules` (`EPERM` on `npm ci`).

## Open

- **gitleaks full-history scan.** Not installed locally. The two CI runs of
  `discovery-validation.yml` on this branch (2026-08-04) ended with no job started,
  which points to Actions being disabled or blocked by account billing, not to a
  workflow error. The branch is 20+ commits ahead of `origin` and has not been
  pushed since. Close by running gitleaks locally or by restoring CI and pushing.
- Not run in T035 by design: SEFAZ homologation calls with the real A1 (done in
  T019, `homologation.md`) and the chiseled base image (foundation item, L3).
