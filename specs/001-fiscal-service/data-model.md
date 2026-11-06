# Data Model: Foundation Design

## Core hierarchy

```text
Tenant 1--* Application
Tenant 1--* Company 1--* Establishment
Establishment 1--* FiscalEnvironment
FiscalEnvironment 1--* FiscalSeries
FiscalEnvironment 1--* FiscalConfiguration
Company/Establishment 1--* DigitalCertificate
Tenant 1--* TaxProfile 1--* TaxRule 1--* TaxRuleVersion
Establishment 1--* FiscalDocument
FiscalDocument 1--* Item/Payment/Reference/Protocol/Event
FiscalDocument 0--* ContingencyRecord
```

## Identity and tenancy

### Tenant

`id`, `name`, `status`, `createdAt`, `updatedAt`. Root scope for quotas, applications and data isolation.

### Application

`id`, `tenantId`, `clientId`, `secretHash`, `scopes`, `allowedEstablishmentIds`, `status`, rotation timestamps. Unique `clientId`; secret is never recoverable.

### Company / Establishment

Company: legal identity, tax ID, regime metadata. Establishment: CNPJ/IE/address/UF/CNAE/CRT and authorization status. Unique tenant + tax ID; environment references establishment.

## Security and configuration

### DigitalCertificate

`id`, owner scope, `vaultRef`, `fingerprint`, subject/tax ID, validity, status, created/rotated/revoked timestamps. No certificate bytes or password in ordinary tables.

### FiscalEnvironment / FiscalConfiguration

Environment is `HOMOLOGATION` or `PRODUCTION`. Configuration references CSC secret, timezone, authorizer catalog version and active schema/rule policies. Production/homologation resources cannot cross.

### FiscalSeries / NumberReservation

Series unique by environment + model + number. Contains monotonic `nextNumber` and optimistic version. Reservation unique by series + number with states `RESERVED`, `BOUND`, `VOID_PENDING`, `VOIDED`, `GAP_REVIEW`.

## Rules

TaxProfile is stable business code. TaxRule uses explicit dimensions; TaxRuleVersion holds immutable payload, validity interval, source references, approval and status. Active intervals cannot overlap for the same dimensions.

## Fiscal document aggregate

FiscalDocument contains tenant, establishment, environment, model, external ID, idempotency record, state, access key, series/number, issue timestamps, snapshots of rule/schema/certificate fingerprint, artifact refs/hashes and version.

Items/payments/references use decimal strings internally mapped to fixed precision. Protocol and official event are append-only. Authorized XML/process protocol cannot be updated, only superseded by an official event relation.

## Operational records

IdempotencyRecord unique by tenant + establishment + operation + key; request hash is immutable. SefazExchange stores sanitized metadata/hashes and encrypted artifact references. WebhookDelivery is at-least-once. AuditLog is append-only. SchemaVersion stores version, hash, validity and source URL.

## State transitions

`DRAFT -> VALIDATING -> READY_TO_SIGN -> SIGNING -> SIGNED -> QUEUED -> SENDING -> WAITING_SEFAZ -> AUTHORIZED|REJECTED`. Technical failure may enter `ERROR` only from retryable stages. `AUTHORIZED -> CANCELLATION_REQUESTED -> CANCELLED`. Result unknown remains nonterminal and is reconciled.

## Validation gates

- tenant consistency across every relationship;
- environment consistency across certificate, CSC, series, endpoint and document;
- unique external ID policy and idempotency hash;
- monotonic series number;
- totals and payments close under declared precision;
- schema/rule version valid at issue time;
- certificate tax ID/validity compatible with issuer;
- immutable authorized artifacts verified by SHA-256.

## PostgreSQL tenant/environment/series constraints

The foundation must encode scope in foreign keys, not only in application
filters. The following is normative design pseudocode for the future migration;
names may be adapted to the selected ORM, but removing the composite keys or
checks requires a new reviewed decision.

```sql
create type fiscal_environment_type as enum ('HOMOLOGATION', 'PRODUCTION');
create type fiscal_model as enum ('55', '65');
create type series_owner_type as enum ('CLOUD', 'LOCAL_AGENT');

create table company (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  tax_id_canonical text not null,
  unique (id, tenant_id),
  unique (tenant_id, tax_id_canonical)
);

create table establishment (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  company_id uuid not null,
  tax_id_canonical text not null,
  unique (id, tenant_id),
  unique (tenant_id, tax_id_canonical),
  foreign key (company_id, tenant_id)
    references company(id, tenant_id)
);

create table fiscal_environment (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment fiscal_environment_type not null,
  status text not null,
  unique (id, tenant_id, establishment_id),
  unique (tenant_id, establishment_id, environment),
  foreign key (establishment_id, tenant_id)
    references establishment(id, tenant_id)
);

create table fiscal_series (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  model fiscal_model not null,
  series integer not null,
  number_min bigint not null,
  number_max bigint not null,
  next_number bigint not null,
  version bigint not null default 0 check (version >= 0),
  allocation_owner series_owner_type not null,
  owner_id uuid,
  owner_epoch bigint not null default 1 check (owner_epoch > 0),
  status text not null,
  unique (id, tenant_id, establishment_id, environment_id, model),
  unique (tenant_id, establishment_id, environment_id, model, series),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  check (number_min <= number_max),
  check (next_number between number_min and number_max),
  check (
    (allocation_owner = 'CLOUD' and owner_id is null) or
    (allocation_owner = 'LOCAL_AGENT' and owner_id is not null)
  )
);

create table fiscal_document (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  series_id uuid,
  model fiscal_model not null,
  external_id text not null,
  access_key text,
  number bigint,
  state text not null,
  request_hash char(64) not null,
  unique (id, tenant_id),
  unique (tenant_id, establishment_id, environment_id, external_id),
  unique (tenant_id, environment_id, access_key),
  unique (series_id, number),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  foreign key (series_id, tenant_id, establishment_id, environment_id, model)
    references fiscal_series(id, tenant_id, establishment_id, environment_id, model),
  check ((series_id is null and number is null) or
         (series_id is not null and number is not null)),
  check (request_hash ~ '^[a-f0-9]{64}$')
);

create table number_reservation (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  series_id uuid not null,
  model fiscal_model not null,
  number bigint not null,
  document_id uuid,
  owner_epoch bigint not null check (owner_epoch > 0),
  state text not null,
  unique (series_id, number),
  unique (document_id),
  foreign key (series_id, tenant_id, establishment_id, environment_id, model)
    references fiscal_series(id, tenant_id, establishment_id, environment_id, model),
  foreign key (document_id, tenant_id)
    references fiscal_document(id, tenant_id)
);

create table idempotency_record (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  operation text not null,
  idempotency_key text not null,
  request_hash char(64) not null,
  resource_id uuid,
  created_at timestamptz not null,
  unique (tenant_id, establishment_id, operation, idempotency_key),
  foreign key (establishment_id, tenant_id)
    references establishment(id, tenant_id),
  foreign key (resource_id, tenant_id)
    references fiscal_document(id, tenant_id),
  check (request_hash ~ '^[a-f0-9]{64}$')
);
```

`access_key` uniqueness treats multiple `NULL` values as unassigned documents.
Once populated it cannot be reassigned. The migration must add an update trigger
or revoke direct update permissions so immutable fields (`tenant_id`,
`establishment_id`, `environment_id`, `series_id`, `model`, `number`,
`access_key`, `request_hash`) cannot change after the document reaches its
signed boundary.

### Atomic number allocation

Allocation is a short database transaction. The worker locks exactly one series
row, verifies that its owner and epoch still match the caller, increments
`next_number` and inserts the reservation before commit.

```sql
begin;
select allocation_owner, owner_id, owner_epoch, next_number
from fiscal_series
where id = :series_id
  and tenant_id = :tenant_id
  and establishment_id = :establishment_id
  and environment_id = :environment_id
  and status = 'ACTIVE'
  and next_number between number_min and number_max
for update;

-- Application compares owner type/id/epoch with its authenticated allocator.
-- A mismatch aborts; an expired connectivity lease does not grant ownership.

update fiscal_series
set next_number = next_number + 1,
    version = version + 1
where id = :series_id
  and owner_epoch = :expected_owner_epoch
returning next_number - 1 as allocated_number;

insert into number_reservation (..., number, owner_epoch, state)
values (..., :allocated_number, :expected_owner_epoch, 'RESERVED');
commit;
```

There is no decrement path. Rollback before commit returns no allocation;
failure after commit leaves an auditable reservation/gap. A local agent must use
an administratively assigned series owner/epoch distinct from cloud allocation;
network loss alone never transfers ownership.

### Idempotency claim

The API first inserts the tuple `(tenant, establishment, operation, key)` and
immutable canonical request hash. On conflict it locks/reads the existing row:
the same hash returns the existing resource/response, while a different hash is
an idempotency conflict. A claim linked to a fiscal document is not expired or
reused merely because an HTTP retention period elapsed.

### Tenant query enforcement

All repositories accept a non-null tenant context and join using composite
keys. PostgreSQL row-level security is recommended as defense in depth:

```sql
alter table fiscal_document enable row level security;
alter table fiscal_document force row level security;
create policy fiscal_document_tenant on fiscal_document
  using (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  with check (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
```

Connections are returned to the pool only after the transaction-local tenant
setting is cleared. Table owners and migration roles are not application roles;
background workers set tenant context from the persisted job and revalidate it
against the document before processing. RLS supplements rather than replaces
composite foreign keys and explicit authorization tests.

### Required constraint tests

- reject company/establishment/environment/series/document cross-tenant links;
- reject a production document linked to a homologation series;
- reject document model different from its series model;
- race two allocators and prove distinct, monotonic reservations;
- reject stale local-agent `owner_epoch` after ownership transfer;
- preserve committed gaps and prohibit `next_number` decrement;
- replay the same idempotency key/hash and return the same resource;
- reject the same idempotency key with a different hash;
- deny reads/inserts when RLS tenant context is absent or belongs to another
  tenant;
- prohibit mutation of signed document identity and artifact snapshot fields.
