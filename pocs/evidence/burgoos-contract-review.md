# BurgoOS fiscal integration contract review

**Status:** REVIEW COMPLETE  
**Checked:** 2026-08-18  
**Reviewed:** idempotency, synchronous/asynchronous acceptance, polling, webhook
reconciliation and artifact retrieval

## Integration ownership

BurgoOS remains the owner of orders, commercial item/payment snapshots and the
operational decision to request emission. Fiscal Service owns fiscal
configuration, numbering, XML/signature, SEFAZ exchange, protocol, DANFE and
official events. BurgoOS stores only the fiscal document identifier/projection
and does not treat its copy as the official artifact store.

## Required BurgoOS records

The integration needs a durable fiscal outbox and webhook inbox. An outbox row
contains tenant, establishment, order, stable `externalId`, persisted
`Idempotency-Key`, canonical request hash, attempt state and returned fiscal
document ID. The key is generated once per intended fiscal mutation and is not
replaced after HTTP timeout, process restart or scheduler retry.

The inbox is unique by Fiscal Service subscription/event ID and stores receipt,
signature-verification outcome and processing state. Duplicate or out-of-order
webhooks are acknowledged idempotently and cause a fresh resource query rather
than a blind state transition.

## Request/response flow

```text
BurgoOS commits order/payment + fiscal outbox atomically
  -> worker POST /documents/async with persisted Idempotency-Key
     -> 202: persist document ID, Location and Retry-After
     -> 200: persist returned projection (same reconciliation rules)
     -> timeout/network loss: reuse the same key; never create a new attempt key
     -> 409 IDEMPOTENCY_CONFLICT: stop and alert; never mutate/retry payload
     -> validation/rejection: persist the domain outcome separately from transport failure

Reconciliation trigger (timer, restart or verified webhook)
  -> GET /documents/{documentId} when ID is known
  -> GET /documents?externalId=... when the create response was lost before ID persistence
  -> respect Retry-After plus bounded jitter for nonterminal state
  -> stop polling at a configured operational horizon but keep the outbox unresolved
```

Polling timeout never changes a document to `REJECTED`, never requests a second
number and never authorizes technical retransmission to SEFAZ. Only Fiscal
Service owns authorizer reconciliation.

## State projection rules

| Fiscal Service observation | BurgoOS action |
| --- | --- |
| Accepted/nonterminal | Keep fiscal projection pending; order/KDS behavior follows explicit product policy |
| `AUTHORIZED` | Store ID, access key/protocol metadata and artifact links; resource query is authoritative |
| Fiscal `REJECTED` | Store rejection code/message as fiscal outcome; do not report infrastructure failure |
| `RESULT_UNKNOWN`/technical unavailable | Keep unresolved and alert according to age; do not mark rejected |
| `CANCELLED` | Update only after authoritative query/event confirms protocolled cancellation |
| Unknown/new status | Preserve last known state, fetch resource and surface compatibility alert |

Status handling must be monotonic according to the versioned public state
machine. Webhook arrival order is not state order.

## Webhook acceptance gates

Before processing a body, BurgoOS must verify the documented signature version,
timestamp tolerance, body hash and per-subscription secret, then deduplicate the
event ID. Verification uses the raw HTTP body. Redirects, JSON reserialization
or parsing before signature verification are forbidden. A verified event is a
reconciliation hint: critical state is confirmed with `GET /documents/{id}`.

Secret rotation needs an overlap window with explicit signature/key version;
delivery retry is at-least-once. BurgoOS returns success for a verified duplicate
already committed to its inbox.

## Contract changes made by this review

- added required `Location` and integer `Retry-After` headers to both `202`
  responses;
- added `GET /documents?externalId=...` for recovery when create succeeded but
  the response/document ID was lost;
- regenerated and type-checked the sample client, including the external-ID
  recovery method.

## Blocking contract gaps

The current OpenAPI still lacks webhook subscription operations and a normative
outbound event/header schema (`eventId`, event type/version, occurrence time,
resource ID, signature version, timestamp and signature headers). It also models
`FiscalDocument.status` as an unrestricted string. These gaps must be resolved
before a BurgoOS webhook consumer is implemented; this review does not invent a
wire format or freeze an incomplete status enum.

The API also needs explicit `429`, `401/403`, polling/nonterminal examples and a
defined compatibility policy. They are foundation contract backlog, not reasons
to weaken idempotency or treat webhooks as authoritative.

## Acceptance scenarios for the future integration test

1. Lose the create response, restart the BurgoOS worker, reuse the same key and
   recover exactly one document by key/resource or `externalId`.
2. Reuse the key with changed payload and confirm a blocking `409` without a
   second fiscal document.
3. Receive duplicate and out-of-order signed webhooks; commit one inbox event and
   converge via GET.
4. Poll through multiple nonterminal responses respecting `Retry-After`, then
   converge to authorization or rejection without changing the request.
5. Reject bad/stale signatures and keep their bodies out of normal application
   logs.
