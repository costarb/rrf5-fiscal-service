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
