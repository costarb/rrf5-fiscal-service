# Quickstart: Fiscal Service Foundation

Development only. No real A1, CSC or secret is used anywhere in this guide.

## Prerequisites

- .NET SDK 10.0 (10.0.302 or later)
- Docker Desktop with the Linux engine

## Local stack

```bash
cd deploy/compose
docker compose up -d --wait        # PostgreSQL, RabbitMQ, SeaweedFS (S3), OpenBao
docker compose ps
docker compose down                # keeps volumes; add -v to wipe them
```

| Service | Host address | Development credential |
| --- | --- | --- |
| PostgreSQL 17.6 | `127.0.0.1:55432`, db `rrf5_fiscal` | `rrf5_owner` / `local-dev-only` |
| RabbitMQ 4.2 | `127.0.0.1:55672`, UI `http://127.0.0.1:55673` | `rrf5` / `local-dev-only` |
| SeaweedFS S3 | `http://127.0.0.1:58333` | open (no identities yet; T035) |
| OpenBao 2.4.1 (dev mode) | `http://127.0.0.1:58200` | token `local-dev-root`; transit enabled by `openbao-init` |

Ports and passwords can be overridden with `RRF5_*` variables (see the compose
file). OpenBao dev mode keeps data in memory: keys are lost on restart.

## Build and test

```bash
dotnet build Rrf5.Fiscal.slnx -c Release      # warnings are errors
dotnet test  Rrf5.Fiscal.slnx -c Release
```

## Images

```bash
docker build -f deploy/docker/Dockerfile --build-arg APP=Api    -t rrf5-fiscal-api:dev .
docker build -f deploy/docker/Dockerfile --build-arg APP=Worker -t rrf5-fiscal-worker:dev .
docker run --rm -p 127.0.0.1:58080:8080 rrf5-fiscal-api:dev    # GET /health/live
```

The two-tenant onboarding walkthrough is added by T038.
