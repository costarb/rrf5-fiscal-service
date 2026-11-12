# Synthetic fiscal profile for the golden NFC-e (T016 input)

**Status:** SUPERSEDED for own production — on 2026-11-12 the accountant validated CFOP 5101 and PIS/COFINS CST 99 (see `fiscal-review.md`). This file stays as the discovery record and the input of the golden XML.  
**Recorded:** 2026-11-06  
**Scope:** NFC-e model 65, São Paulo, homologation, issuer's own matriz

This file records the fiscal choices supplied by the operator so that the POCs
can generate a deterministic golden XML. It is not tax advice and not a tax rule
engine. Issuer identifiers (CNPJ, IE, address) stay in configuration outside Git.

## Sources

| Source | Date | What it establishes |
| --- | --- | --- |
| Receita Federal "Consulta Optantes" (screenshot shown by the operator, not stored) | 2026-11-06 10:57 | Simples Nacional optant since 2026-04-24; **not** SIMEI; not optant for the regular IBS and CBS regimes |
| Operator answers in the discovery session | 2026-11-06 | CRT, CFOP, PIS/COFINS CST and IBS/CBS group decision (below) |
| Operator product table (screenshot, not stored) | 2026-11-06 | NCM, unit, origin, GTIN per product family |
| Bling homologation XMLs (outside Git, see `homologation.md`) | 2026-10-30 / 2026-11-06 | Structural reference only; **not** a source of tax choices (see divergences) |

## Profile `SP-SN-OWN-PRODUCTION-V1` (golden XML scope)

| XML field | Value | Basis |
| --- | --- | --- |
| `emit/CRT` | `1` (Simples Nacional) | RFB consultation + operator |
| `ide/tpNF` / `indFinal` / `indPres` / `idDest` | `1` / `1` / `1` / `1` | Operator (presential sale to final consumer, in-state) |
| `prod/NCM` | `21069090` | Operator table (sandwiches, own production) |
| `prod/cEAN`, `cEANTrib` | `SEM GTIN` | Operator table |
| `prod/uCom`, `uTrib` | `UN` | Operator table |
| `prod/CFOP` | `5102` | Operator answer (overrides the `5101` in the earlier table) |
| `ICMS/ICMSSN102/orig` | `0` | Operator table |
| `ICMS/ICMSSN102/CSOSN` | `102` | Operator table |
| `PIS` / `COFINS` | `PISOutr` / `COFINSOutr`, CST `49`, `vBC` 0.00, rate 0.0000, value 0.00 | Operator decision 2026-11-06: keep as configured in Bling (supersedes the `99` in the earlier table) |
| `CEST`, ICMS-ST, FCP, `cBenef` | not informed | Operator table |
| `IBSCBS` / `IBSCBSTot` groups | **not sent** in 2026 | Operator decision 2026-11-06; to be confirmed against NT 2025.002 at the accountant meeting |
| `vTotTrib` (Lei 12.741/2012) | **OPEN** | Operator uses IBPT by NCM; source/version not chosen |

## Out of golden scope (recorded, not used yet)

| Family | NCM | CFOP | CSOSN | Notes from operator table |
| --- | --- | --- | --- | --- |
| Resale without ST (mineral water) | 22011000 | 5102 | 102 | PIS/COFINS "per product; validate" |
| Resale with ST (soft drinks) | 22021000 | 5405 (Bling rule) / 5105 (table) | 500 | CEST, ST retained and PIS/COFINS (typically 04 monophasic) to confirm |

The ST case conflicts between sources and needs CEST/ST-retained fields; it is a
second golden case only after the accountant resolves it.

## Open items (block closing T011)

1. **Accountant ratification** of this whole profile, including the IBS/CBS
   omission for a CRT 1 issuer in 2026 and the PIS/COFINS CST 49 choice. The
   omission is an operator decision, not something the POC derived from
   NT 2025.002; the accountant should cite the applicable rule.
2. **`vTotTrib` source.** Choose and version the IBPT (or other) table; record
   licence and update cadence in `dependency-review.md`.

## Divergences found in the Bling configuration (operator action)

| Item | Bling XML | Profile |
| --- | --- | --- |
| CRT | `4` (MEI) in the 2026-10-30 documents; `1` on 2026-11-06 | `1` |
| `IBSCBS` groups | filled automatically (CST 000, cClassTrib 000001) | not sent |

Bling's homologation documents therefore remain structural references only.
