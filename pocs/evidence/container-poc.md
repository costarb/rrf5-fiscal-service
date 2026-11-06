# Linux container POC (.NET) — portability gate

**Status:** PASS for local operations and TLS trust; real A1 call from the container pending (operator)  
**Executed:** 2026-11-06, Docker 29.9.2 (Linux engine) on the Windows development host

## Image

- `pocs/dotnet/Dockerfile`: multi-stage, `mcr.microsoft.com/dotnet/sdk:10.0` →
  `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04.5 LTS, .NET 10.0.14).
- Runs as the non-root `app` user (uid 1654). Size 204 MB.
- Only `*.cs` and the `.csproj` are copied. Certificates, passwords, schema
  packages, trust roots and fiscal documents are mounted read-only at run time;
  `pocs/dotnet/.dockerignore` also excludes `*.pfx`, `*.p12`, `*.xml`, `*.zip`.
- The "inside the repository" guard now looks for the nearest `.git` ancestor;
  in the image there is none, so mounted output paths are allowed while the
  guard keeps working on the development host (re-tested: `OUTPUT_INSIDE_REPOSITORY`).

## Local operations in the container

Inputs mounted: `pocs/fixtures` (ro), `sefaz/schemas/schemas-010e` (ro), a
scratch work directory (rw), a throwaway self-signed PFX (deleted afterwards).

| Check | Result |
| --- | --- |
| `generate` synthetic golden | SHA-256 `82e3ab6f…`, **byte-identical** to the Windows golden |
| `sign` (throwaway PFX) | verified; mutation rejected |
| `validate` against 010e v1.02 | valid |
| `authorize --mock authorized` | `AUTHORIZED`, response hash identical to the Windows run |
| `authorize --mock timeout` | `RESULT_UNKNOWN`, exit 3 |

## Finding: SEFAZ-SP server chain is not trusted by Linux images

The homologation host presents `CN=homologacao.nfce.fazenda.sp.gov.br`, issued
by `AC SOLUTI SSL EV G4`, under **ICP-Brasil "Autoridade Certificadora Raiz
Brasileira v10"**. Windows ships this root; the Debian/Ubuntu CA bundle used by
the .NET images does not (`openssl s_client`: verify code 20; `curl`: error 60).

Resolution (validation is never disabled):

- Root obtained from ITI (`http://acraiz.icpbrasil.gov.br/credenciadas/RAIZ/ICP-Brasilv10.crt`;
  the HTTPS endpoint itself uses an ICP-Brasil chain). Because the download is
  plain HTTP, its SHA-256 was cross-checked against the Windows trusted root store:
  both are `6e0bff069a26994c15de2c4888cc54af84882e5495b7fbf66be9ccffec7489f6`.
  Valid 2019-07-01 to 2032-07-01. Stored locally in `sefaz/trust/` (ignored by Git).
- `RRF5_SEFAZ_TRUST_ROOT=<path>` makes the client use `CustomRootTrust` with that
  single root, and refuses any file whose SHA-256 differs from the pinned value.

| `status` in the container with a throwaway client certificate | Result |
| --- | --- |
| no trust root (OS store) | `UntrustedRoot` — TLS refused by the client |
| pinned ICP-Brasil v10 root | server TLS accepted; SEFAZ answered HTTP 403 to the non-ICP client certificate (expected) |
| a different certificate as root | `TRUST_ROOT_REFUSED` before connecting |

Design consequences for the foundation:

- SEFAZ trust anchors are configuration, versioned and pinned, separate from the
  general OS trust store; the v10 expiry (2032) and future ICP-Brasil roots need
  an update path. Other authorizers may chain to other roots.
- Production images must not "fix" this by disabling validation or by adding
  ICP-Brasil to the global store for all outbound traffic.

## Pending

- `status`/`authorize` from the container with the real A1 (operator run).
- Container build in CI and a Java/TypeScript equivalent.
