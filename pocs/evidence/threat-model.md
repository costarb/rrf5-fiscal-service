# Fiscal Service threat model

**Status:** COMPLETE FOR DISCOVERY  
**Reviewed scope:** A1, CSC, tenant isolation, webhooks and fiscal artifact storage  
**Method:** trust-boundary review plus STRIDE-style threat enumeration  
**Checked:** 2026-08-18

## Security objectives

1. A tenant can access and operate only its explicitly bound establishments.
2. A1 private material, passwords and CSC values are never disclosed through
   API responses, logs, traces, images, queues, source or ordinary database rows.
3. Signed/authorized fiscal artifacts remain authentic, immutable and attributable.
4. Webhook receivers can verify origin, freshness and uniqueness without the
   signing secret leaving its trust boundary.
5. Environment, issuer, series and schema/rule versions cannot be silently
   substituted across an emission attempt.

## Assets and classification

| Asset | Confidentiality | Integrity/availability concern |
| --- | --- | --- |
| A1 PKCS#12 bytes and password | Critical secret | Theft enables impersonation; loss blocks emission |
| CSC identifier/token | Critical secret | Disclosure enables forged/abusive QR material |
| Tenant/establishment bindings | Restricted | Corruption enables cross-tenant fiscal operations |
| Normalized request and tax configuration snapshot | Restricted | Mutation changes fiscal meaning |
| XML, protocol, events and DANFE | Fiscal/personal restricted | Authorized artifacts must be immutable and retained |
| Webhook secret and delivery body | Secret/restricted | Forgery or replay misleads client systems |
| Audit trail, artifact hashes and certificate fingerprint | Restricted metadata | Must be append-only and attributable |

## Trust boundaries

```text
Client/PDV -> API/Auth -> Application/tenant context -> PostgreSQL
                                |                  -> queue -> worker
Operator UI -> privileged API --+                              |-> KMS/Vault
                                                               |-> object storage
                                                               |-> SEFAZ
Worker -> outbound webhook dispatcher -> tenant-controlled destination
Telemetry pipeline <- API/application/worker (sanitized metadata only)
```

Every arrow crosses an authenticated or policy-enforced boundary. Tenant ID is
derived from the authenticated principal and server-side binding, never trusted
from a body, queue message or object-storage key supplied by the client.

## Threat register

| ID | Threat/scenario | STRIDE | Required controls | Verification evidence | Residual risk |
| --- | --- | --- | --- | --- | --- |
| TM-01 | Attacker downloads or exports an A1 | Information disclosure | No export endpoint; envelope encryption; per-certificate data key; KMS/Vault policy restricted to worker identity | API negative tests; IAM review; encrypted backup inspection | Privileged worker compromise can use decrypted material in memory |
| TM-02 | A1/password appears in logs, crash dump or telemetry | Information disclosure | Structured allowlist logging; secret types excluded; exception sanitization; dump policy; short-lived buffers | Canary-secret tests across logs/traces; repository and image scans | Runtime/host compromise may inspect memory |
| TM-03 | Certificate replaced with another taxpayer's A1 | Spoofing/tampering | Verify format, chain, validity, fingerprint and approved titularity binding; dual-control rotation; audit | Mismatch/rotation tests and attributable audit entry | CA or identity-proofing compromise |
| TM-04 | CSC is read from database/config or exposed in QR diagnostics | Information disclosure | Secret-store reference only; least-privilege resolve at generation; never log URL/token; rotate/version | DB snapshot review; canary scan; redacted QR diagnostics | Authorized worker necessarily uses CSC transiently |
| TM-05 | Client changes tenant/establishment ID to access another company | Elevation/information disclosure | OAuth scope plus server-side application-establishment binding; tenant predicate on every repository query; deny by default | Cross-tenant matrix for create/read/cancel/download/admin | Query added outside guarded repository could bypass policy |
| TM-06 | Queue message/object key is replayed under another tenant | Spoofing/tampering | Signed/internal authenticated envelope; immutable tenant, environment, document and request hash; worker reloads authority from DB | Tampered-message and cross-tenant worker tests | Broker administrator compromise |
| TM-07 | Production/homologation or issuer/series is substituted | Tampering/elevation | Environment and issuer fixed in idempotency snapshot; separate credentials/endpoints; DB constraints; transition checks | Environment-confusion and series-ownership tests | Misconfiguration approved by privileged operator |
| TM-08 | Authorized XML/PDF is overwritten or served from a predictable public URL | Tampering/disclosure | Private bucket; versioning/object lock where supported; content hash; immutable references; short signed download URLs with active authorization | Bucket-policy test; overwrite denial; hash-on-read; URL expiry test | Storage/KMS administrator collusion |
| TM-09 | Malicious XML triggers XXE/entity expansion or signature wrapping | Tampering/DoS/elevation | DTD/external entity disabled; schema pinned by hash; size/depth limits; verify exact signed ID/reference and reject duplicate IDs | XXE/billion-laughs/wrapping/duplicate-ID negative corpus | Parser/library zero-day |
| TM-10 | Schema or tax-rule downgrade accepts obsolete content | Tampering | Catalog hash and applicability window; immutable version snapshot; signed release/approval path; fail closed if unresolved | Downgrade and unknown-version tests | Incorrect official applicability configuration |
| TM-11 | Attacker registers webhook to metadata/internal endpoint (SSRF) | Spoofing/information disclosure | HTTPS policy; resolve and block loopback/private/link-local/cloud metadata ranges before each attempt; redirect disabled/revalidated; egress policy | DNS rebinding, redirect and metadata-IP tests | Public endpoint later changes behavior |
| TM-12 | Webhook is forged or replayed | Spoofing/replay | HMAC-SHA256 over versioned canonical input; timestamp tolerance; unique event ID; secret rotation overlap; consumer deduplication | Signature mutation, stale timestamp and duplicate-event tests | Receiver mishandles verification or stores secret insecurely |
| TM-13 | Webhook body or status leaks another tenant's document | Information disclosure | Delivery record bound to tenant/subscription; event payload minimizes PII; resource lookup requires authorization | Cross-subscription delivery tests; payload review | Tenant endpoint forwards data onward |
| TM-14 | Insider reads fiscal artifacts/secrets without business need | Repudiation/disclosure | Separate roles; just-in-time privileged access; dual control for secret recovery; immutable audit and alerts | Quarterly access review; break-glass exercise | Authorized malicious insider |
| TM-15 | Deletion/ransomware removes fiscal evidence | DoS/tampering | Versioned immutable storage; encrypted backups; separate credentials/account; restore exercises; retention/legal hold | Restore test and delete-denial test | Coordinated compromise of production and backup control planes |
| TM-16 | Idempotency race creates duplicate documents/numbers | Tampering/repudiation | Unique DB constraints; request hash; transactional series lock; consult-before-retry | Concurrency and uncertain-result tests | External authorizer inconsistency |

## Abuse cases that must fail closed

- certificate secret reference exists but KMS/Vault authorization is unavailable;
- authenticated application is not explicitly bound to the establishment;
- tenant context differs between request, persisted document and queue job;
- object hash differs from the immutable database record;
- webhook host resolves to a forbidden network range or changes after redirect;
- schema/rule version cannot be resolved by document issue time;
- signature contains an unexpected reference, transform or duplicate XML ID;
- emission response is lost and consultation is inconclusive.

None of these conditions may fall back to a default tenant, certificate, schema,
environment, retry or official contingency mode.

## Security test gates for foundation

- automated cross-tenant tests for every document, certificate, configuration,
  download, webhook and administrative route;
- canary A1/CSC/password values verified absent from source, Git history, build
  context, container layers, logs, traces, queue payloads and ordinary DB dumps;
- XML parser and XMLDSig adversarial corpus, including XXE, entity expansion,
  signature wrapping, duplicate IDs and post-signature mutation;
- webhook SSRF/replay/signature/rotation suite;
- object-store policy, immutability, hash verification, backup and restore tests;
- KMS/Vault IAM and break-glass review with attributable audit evidence;
- dependency/SBOM scan and patched-runtime policy for crypto/XML components.

## Accepted discovery limitations

This branch contains no real A1, CSC, production tenant or authorized fiscal
artifact, so it cannot prove cloud IAM, KMS memory exposure, object lock or
runtime telemetry behavior. Those are entry gates for the foundation and must
be retested in the selected deployment environment. No residual risk here is an
approval to weaken official fiscal or security requirements.

## Foundation retest on the local stack (2026-11-11, T039 of 002)

Scope: feature 002 code on the local stack (PostgreSQL 17.6, OpenBao 2.4.1 dev,
SeaweedFS 3.97, chiseled images). There is no emission, queue consumer, webhook or
cloud deployment yet, so items that depend on them are marked not reachable.
Evidence names are test classes under `tests/` (suite green in CI and on a fresh
clone with a fresh stack).

| ID | Result | Evidence | Open item |
| --- | --- | --- | --- |
| TM-01 | **Partial** | No route returns certificate bytes, password or CSC (`ContractTests` route list); envelope encryption with a per-secret data key from transit and AES-256-GCM bound to tenant/kind/id; only ciphertext in KV (`SecretCanaryTests`, `CertificateApiTests`) | The API and worker use the OpenBao dev root token; a vault policy restricted to the worker identity is a deployment item (checklist B, L1) |
| TM-02 | **Pass (local)** | Password and CSC canaries absent from responses, logs, spans, every table and vault entries (`SecretCanaryTests`, mutation-checked); allowlist logging and span redaction (`TelemetryRedactionTests`); every image layer scanned in CI (`scripts/scan_image_layers.py`) | Crash-dump policy and memory exposure belong to the deployment environment |
| TM-03 | **Partial** | Holder CNPJ-Base equals the establishment CNPJ-Base (MOC F03 / 213), validity window, single RSA key, SHA-256 fingerprint, audit of each upload (`CertificateInspectionTests`, `CertificateApiTests`) | **ICP-Brasil chain validation is not implemented** and rotation is single-control (any `certificates.manage` application). Both must be decided before production |
| TM-04 | **Pass (storage)** | CSC only as a vault reference; reads expose `cscConfigured` only; no read endpoint (`CertificateApiTests`, `SecretCanaryTests`) | QR generation and its diagnostics come with emission |
| TM-05 | **Pass** | Cross-tenant matrices for tenancy, issuer, vault, rules and schema routes answer 404 (`CrossTenantTests`, `IssuerIsolationTests`, `TaxRuleLifecycleTests`, `SchemaSelectionApiTests`, `TwoTenantOnboardingTests`); explicit tenant predicates plus forced RLS (`RowLevelSecurityTests`) and composite keys (`ConstraintTests`) | Document, download and webhook routes do not exist yet |
| TM-06 | Not reachable | No queue consumer in the foundation | Phase 2 |
| TM-07 | **Pass (data)** | Database refuses cross-environment and cross-model links; one environment of each kind; certificates and CSC bound to one environment (`ConstraintTests`, `IssuerApiTests`) | Endpoint selection per environment comes with the SEFAZ adapter |
| TM-08 | **Partial** | Official artifacts write-once by SHA-256, versioned bucket, COMPLIANCE object lock, hash verified on write and every read; tamper and version delete refused (`RegulatoryStorageTests`) | Private-bucket policy (the local store accepts any credential) and authorized XML/DANFE storage come with deployment and emission |
| TM-09 | Not reachable | No XML parsing in the foundation | Phase 2 corpus |
| TM-10 | **Pass** | Only VERIFIED packages load; a changed hash is refused; selection fails closed (`schema.not_resolved`); exclusive elements need an audited release (`SchemaSelectionTests`, `RegulatoryStorageTests`, `SchemaSelectionApiTests`); approved rule versions are immutable, non-overlapping and non-retroactive (`TaxRuleConstraintTests`, `TaxRuleLifecycleTests`) | — |
| TM-11 to TM-13 | Not reachable | No webhooks | Before the BurgoOS consumer (checklist C) |
| TM-14 | **Partial** | Author and approver are separate applications enforced in app and database; runtime roles without DDL or BYPASSRLS; append-only audit with principal and correlation (`RowLevelSecurityTests`, `IdentityDatabaseTests`, `IdempotencyAndAuditTests`) | Just-in-time access, break-glass and access review belong to operations |
| TM-15 | **Partial** | Deletion of a locked artifact version refused; audit and artifact records append-only | Backups, restore exercise and separate control planes belong to deployment |
| TM-16 | **Pass** | 1,000 concurrent allocations from two instances, no repeat or decrease; idempotent claims under concurrency (`NumberAllocationTests`, `IdempotencyAndAuditTests`) | Consult-before-retry comes with emission |

Fail-closed cases exercised: vault unavailable on token issue (503) and validation
(401); vault unavailable on certificate upload (503, no row); application without
an establishment grant (404); schema not resolvable (404 `schema.not_resolved`);
stored object hash differing from the record (`artifact.integrity_failed`).
