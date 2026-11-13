# Fiscal stakeholder review

**Status:** PARTIAL — accountant validated the per-product table on 2026-11-12; answers on IBS/CBS and resale-without-ST PIS/COFINS relayed on 2026-11-13; the `vTotTrib` source and the rule citation for the IBS/CBS omission remain open

This record must be completed by the responsible fiscal stakeholder after the official artifacts and hashes in `normative-baseline.md` are frozen.

## Interim operator approval (not the T011 approval)

- Date: 2026-11-06
- Approver: issuer's owner/operator (identity kept outside Git; same person who
  controls the A1 recorded in `homologation.md`)
- Scope: the synthetic profile `SP-SN-OWN-PRODUCTION-V1` in `fiscal-profile.md`
  (CRT 1, CFOP 5102, CSOSN 102, PIS/COFINS CST 49 at zero, no IBS/CBS groups in
  2026), for **discovery POCs and homologation only**
- Basis: Receita Federal Simples Nacional consultation of 2026-11-06 and the
  operator's own decisions; not yet reviewed by the accountant
- Condition: no production emission and no T011 completion until the
  accountant confirms or corrects every item, citing the applicable rule for the
  IBS/CBS omission

## Accountant review of the per-product table (2026-11-12)

- Date: 2026-11-12, relayed by the operator in the working session
- Evidence: the validated table, archived outside Git as
  `rrf5-private/fiscal/accountant-table-2026-11-12.png`
  (SHA-256 `16b7589d05e0dd5e460993ac65b775c68ebc399c0e5b032bfc13e7679e1c5771`)
- Accountant identity: kept outside Git with the evidence

| Field | Own production (21069090) | Resale without ST (22011000) | Resale with ST (22021000) |
| --- | --- | --- | --- |
| Unit / GTIN / origin | UN / SEM GTIN / 0 | UN / SEM GTIN / 0 | UN / SEM GTIN / 0 |
| CEST | not informed | not informed | per packaging/product |
| CFOP | **5101** | 5102 | 5405 |
| CSOSN | 102 | 102 | 500 |
| ICMS BC and rate highlighted | no | no | no |
| ICMS-ST / FCP | no | no | retained earlier |
| cBenef | empty | empty | per specific rule |
| PIS / COFINS | **CST 99 / 0%** | per product; **validate** | usually 04 / 0% (monophasic) |
| Monophasic PIS/COFINS | no | depends on the product | yes, typical soft drink |
| `vTotTrib` (Lei 12.741/2012) | IBPT by NCM | IBPT by NCM | IBPT by NCM |

### Effect on the interim profile

- **Own production reverses two interim choices:** CFOP 5102 becomes **5101**, and PIS/COFINS CST 49 becomes **99**.
  - The operator had overridden the original table on 2026-11-06 to match Bling.
  - The accountant validated the original values, and the validated table prevails (AGENTS.md).
  - The DRAFT imported in 002 (5102/49) must not be approved; a new version with 5101/99 replaces it.
- **Resale with ST:** the 5105 vs 5405 conflict is resolved to **5405**.
  - CSOSN 500 needs the `ICMSSN500` group, CEST and the retained-ST fields.
  - Operator decision on 2026-11-12: deferred to a later generator increment; refused with a reason until then.
- **Resale without ST:** PIS/COFINS was "validate" in the table; answered on 2026-11-13 (CST 99, 0%), see below.
- **Not covered by the table (still open):**
  1. the IBS/CBS omission for CRT 1 in 2026, with the applicable rule cited (decision relayed on 2026-11-13, citation still open);
  2. the IBPT table: version, licence and update cadence;
  3. the accountant's attributable sign-off on the normative baseline (T011 scope).

## Accountant answers relayed on 2026-11-13

Relayed by the operator in the working session, about the items the table left open.

- **IBS/CBS:** the company is not an optant (it does not opt for paying IBS/CBS outside the Simples Nacional) and will stay that way. **In 2026 the IBS/CBS groups are not sent.**
  - This matches the rules' payload (`ibsCbs.send = false`).
  - Still open: the citation of the applicable rule, required by the interim approval condition above.
- **PIS/COFINS of resale without ST (22011000):** **CST 99 at a 0% rate**, the same as own production.
  - A rule for 22011000 (5102/102/99) can now be drafted. It is approved only through the four-eyes flow (ADR-015), like any other.
- **`vTotTrib` (Lei 12.741/2012):** still open. The operator asked what is needed; the options are recorded in feature 005 (Phase 7) and the decision stays with the operator and the accountant.

## Reviewer

- Name/role:
- Organization:
- Review date:
- Evidence revision/commit:

## Decisions

- Baseline versions and effective dates accepted: pending
- NFC-e/SP applicability accepted: pending
- RTC assumptions accepted or corrected: pending
- Contingency interpretation accepted: pending
- Gaps/blockers:

## Approval

- Decision: pending
- Conditions:
- Signature or auditable approval reference:

Do not mark T011 complete until this record contains an attributable approval.

