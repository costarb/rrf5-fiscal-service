# NFC-e/SP contingency gates and non-goals

**Status:** COMPLETE FOR DISCOVERY  
**Checked:** 2026-08-18  
**Scope:** NFC-e model 65, São Paulo

## Official applicability snapshot

The SEFAZ-SP contingency page states that Portaria SRE 40, dated 2024-07-05,
authorizes offline contingency in São Paulo. The national NFC-e offline
contingency manual v2.0 was frozen in this discovery with SHA-256
`5ff5c8fdc8a55ae91f2f6e7c29566a45ca7e3867c7b76877deb48911071cf06c`.

SEFAZ-SP also lists EPEC separately. Its page states that production EPEC is
activated by SEFAZ only during normal authorization-environment unavailability,
while homologation remains available. EPEC is therefore not an application
controlled generic fallback and must not be conflated with offline contingency.

These statements establish discovery applicability only. Before production,
the responsible fiscal stakeholder must review the current Portaria, manuals,
technical notes, taxpayer eligibility and operational configuration.

## Mandatory product gates

No component may enter an official contingency state unless all of these are
true:

- document model, UF, environment and issuer configuration resolve to a current
  officially supported mode;
- the responsible fiscal stakeholder approved the mode-specific configuration;
- the original access key, number, issue time, mode and immutable XML are
  persisted durably before a printable artifact is released;
- XML, signature, DANFE/QR and later transmission/reconciliation gates passed;
- certificate/CSC custody, clock, storage capacity and audit logging are healthy;
- activation reason and operator/system authority are attributable;
- reconnection preserves the original artifact and consults an uncertain result
  before any retransmission.

Failure of any gate is fail-closed and reported as fiscal emission unavailable.
It is not silently converted to normal retry or another contingency mode.

## Explicit non-goals for the discovery branch

- no production activation of offline contingency or EPEC;
- no claim that a cloud queue solves PDV-to-service connectivity loss;
- no automatic EPEC activation by the application;
- no generic switch from normal emission to offline mode on HTTP timeout;
- no new number/access key/XML after printing or an uncertain response;
- no promise that browser storage is a compliant local fiscal agent;
- no automatic correction, cancellation or authorization of a document rejected
  after reconnection;
- no fixed deadlines, eligibility or tax treatment copied into code without a
  versioned official source and fiscal approval;
- no reuse of NF-e model 55 SVC/EPEC behavior as NFC-e model 65 behavior merely
  because both use the term contingency.

## Current product behavior

The foundation MVP is cloud-only. When the PDV cannot reach the Fiscal Service,
new fiscal emission is unavailable. When the Fiscal Service reaches SEFAZ but
loses a response, the document remains `RESULT_UNKNOWN` and follows the
consult-before-retry POC. Neither behavior is presented as offline contingency.

A managed local agent may be evaluated later under the gates in
`offline-agent-spike.md`; that spike is architecture evidence, not authorization
to implement or operate a fiscal contingency mode.

## Sources

- SEFAZ-SP contingency page, checked 2026-08-18:
  `https://portal.fazenda.sp.gov.br/servicos/nfce/Paginas/Conting%C3%AAncia.aspx`
- National NFC-e offline contingency manual v2.0: catalog entry
  `nfce-contingencia-offline` in `regulatory/catalog.json`
- Offline-agent boundary: `pocs/evidence/offline-agent-spike.md`
- Uncertain-result simulation: `pocs/shared/uncertain-result/`
