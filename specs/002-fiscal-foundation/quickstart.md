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

## Database

Migrations live in `db/migrations/` (SQL, ADR-014), are embedded in
`Rrf5.Fiscal.Infrastructure` and applied with the owner connection:

```bash
ConnectionStrings__Owner="Host=127.0.0.1;Port=55432;Username=rrf5_owner;Password=local-dev-only;Database=rrf5_fiscal" \
  dotnet run --project src/Rrf5.Fiscal.Worker -- migrate
```

An applied migration is never edited: the migrator refuses a changed hash. The
runtime roles `rrf5_app` and `rrf5_worker` are created `NOLOGIN`; their logins
are provisioned per environment, outside the repository. Database tests create
and drop their own `rrf5_test_*` database on the same server (override with
`RRF5_TEST_PG`), so the stack must be up before `dotnet test`.

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
