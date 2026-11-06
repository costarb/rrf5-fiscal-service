# PDV-to-service connectivity and local-agent spike

**Status:** COMPLETE FOR DISCOVERY  
**Checked:** 2026-08-18  
**Scope:** architecture only; this document does not enable fiscal contingency

## Failure boundary

A durable queue inside the cloud Fiscal Service does not help when the PDV
cannot reach that service. In that condition the cloud cannot allocate a
number, sign or return a printable artifact. Continuing sales with fiscal
offline behavior therefore requires a trusted component on the establishment
side; otherwise the safe behavior is to block fiscal emission and expose an
operational error.

## Alternatives

| Alternative | Behavior during PDV/cloud link loss | Security/operations | Result |
| --- | --- | --- | --- |
| Cloud-only MVP | Rejects new emission requests locally; resumes normal flow after connectivity returns | Smallest attack surface; no local A1/CSC or fiscal store | **Selected for foundation MVP** |
| Managed local fiscal agent | Accepts normalized requests, owns a durable local outbox, allocates from an assigned series, generates/signs the original XML and supports only an officially enabled mode | Requires hardened lifecycle, encrypted secrets/storage, clock monitoring, support and reconciliation | **Viable follow-up spike** |
| Fiscal logic embedded in each PDV | Every client owns numbering, schemas, certificate access and reconciliation | Multiplies secret exposure, upgrade drift and collision risk | **Rejected** |
| Browser/service-worker queue | Stores commercial requests until the cloud returns | Cannot safely provide fiscal numbering, A1 signing or a compliant printable artifact by itself | **Not fiscal contingency** |

## Local-agent boundary

If the follow-up agent is approved, it must be a separately versioned and
managed process. The PDV talks to it over authenticated localhost or a mutually
authenticated LAN channel. The agent exposes a narrow API compatible with the
cloud request identifier but never exposes certificate bytes, CSC values or
private keys.

The minimum durable records are:

- tenant, establishment, environment and an agent identity;
- client idempotency key plus immutable normalized-request hash;
- explicitly assigned series and monotonic number state;
- original XML/hash, mode, issue time and timezone;
- print acknowledgement without treating printing as authorization;
- transmission attempts and sanitized response/protocol metadata;
- reconciliation state and an append-only audit trail.

Artifacts and secrets must be encrypted at rest with distinct keys. Certificate
rotation, agent revocation, storage exhaustion, clock drift and software update
failure all fail closed and raise an operational alert.

## Ownership and split-brain prevention

Cloud and agent must never allocate from the same establishment/environment/
series range concurrently. Activation requires a leased or administratively
assigned series ownership record with an auditable epoch. An expired network
lease alone is insufficient authority to start fiscal offline operation.

The same client idempotency key and normalized-request hash travel from PDV to
agent and later to cloud. Reusing a key with different content is rejected.
Reconnect uploads the original immutable artifact and attempt history; it does
not regenerate a document or decrement/reuse a number.

## Reconnection state machine

```text
LOCAL_ACCEPTED -> LOCAL_ARTIFACT_READY -> PENDING_TRANSMISSION
PENDING_TRANSMISSION -> TRANSMITTING -> AUTHORIZED | REJECTED | RESULT_UNKNOWN
RESULT_UNKNOWN -> CONSULTING -> AUTHORIZED | REJECTED | RESULT_UNKNOWN
AUTHORIZED | REJECTED -> RECONCILED_WITH_CLOUD
```

`RESULT_UNKNOWN` always consults the applicable official service before any
retransmission. A later rejection remains a fiscal/operational exception and is
never rewritten as success.

## Entry gates for a future agent

- responsible fiscal stakeholder approves applicability, activation and exit
  rules for NFC-e/SP against the frozen contingency manual;
- security review approves A1/CSC custody, local encryption and update signing;
- deterministic golden XML, XMLDSig, DANFE/QR and reconciliation POCs pass;
- chaos tests cover power loss at every state transition, duplicate delivery,
  clock drift, storage exhaustion and cloud/agent split brain;
- support runbook defines activation authority, monitoring, recovery and
  escalation for documents rejected after reconnection.

Until every gate is met, product/UI language must say that fiscal emission is
unavailable when the PDV cannot reach the cloud Fiscal Service. It must not call
technical queuing an official contingency mode.
