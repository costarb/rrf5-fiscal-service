# Phase 1 — Normative gates (005)

Record of the checklist B items handled in Phase 1. The normative bytes stay outside Git, in `rrf5-private\sefaz`; this file keeps only the hashes and section references.

## Frozen copies used

| Document | Version found | SHA-256 | Location (outside Git) |
| --- | --- | --- | --- |
| MOC 7.0 Anexo I — Leiaute e Regras de Validação da NF-e e da NFC-e | **7.00, November 2020** (cover) | `5eb4cf2010b10b0b62f78197c4eb64025f24535d4c6e61158bc7806dd008f55d` | `sefaz\moc\MOC_Anexo_I_Leiaute.pdf` |
| Schema package 010g v1.03 | 2026-10-26 | `b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf` (catalog `xsd-nfe-010g-1-03`) | `sefaz\schemas\PL_010g_v1.03.zip` |
| ITI ICP-Brasil bundle | 2026-11-04 | catalog `icp-brasil-ac-bundle` | `icp-brasil\ACcompactado.zip` |

The Anexo I copy is the 2020 edition. Rules changed by later technical notes are not in it. It is not used as the current source for anything those notes changed (see T003).

## T001 — Golden XML against 010g v1.03: done

- `pocs/fixtures/golden/nfce-minimal-sp-sn-v1.unsigned.xml` is pinned by SHA-256 `82e3ab6f83b85555f551d597afad93cba6a9d6557ad03b4ac34e77b255a504b1`. No regeneration was needed.
- It carries no element exclusive to 010g. The check uses the official element diff in `PackageExclusiveElements`.
- With the official ZIP on the machine, the ZIP hash matches the catalog. The golden is schema-valid; the only error is the missing `ds:Signature`, because the golden is unsigned and signing is T015.
- Evidence: `GoldenXmlTests`. The XSD check runs only where the ZIP is present, like `IcpBrasilBundleTests`.

## T004 — Rounding: done (technical choice to confirm)

- **Official rule:** MOC 7.0 Anexo I, footnote (\*4) of rules I11 (rejections 629 and 630). The product vUnCom × qCom and the product vUnTrib × qTrib "deve ser arredondado para um valor numérico com duas casas decimais", and SEFAZ applies a tolerance of R$ 0,01 either way.
- **Totals:** vNF is the sum defined by rule W16 (rejection 610).
- **What the manual leaves open:** it does not fix the rounding mode.
- **Choice:** half away from zero (`FiscalRounding`). Under the official tolerance, half-up and half-even results are both accepted, so this is a technical choice, not a tax rule. The generator never relies on the tolerance: totals are exact sums of values already rounded to two decimals.
- Evidence: `FiscalRoundingTests`, with the POC boundary cases.
- **Rule for T010:** NFC-e rules 750–752 (W16) require consumer identification above the value limit. The pre-validation implements them from the frozen text and the current notes; the values are not hard-coded from this copy.

## T005 — ICP-Brasil root pinned per authorizer: done

- SEFAZ-SP: "Autoridade Certificadora Raiz Brasileira v10", SHA-256 `6e0bff06…7489f6`. This is the root the POC proved in homologation runs 3 and 4, cross-checked on 2026-11-06 against the ITI download and the Windows root store.
- `Authorizer.TlsRootSha256` holds the pin. `SefazTlsTrust.PolicyFor` builds a `CustomRootTrust` policy with only that root, taken from the hash-verified ITI bundle; the operating system store is not used. A missing root fails closed.
- Evidence: `IcpBrasilBundleTests` (the pinned root is in the official bundle and is the only trust anchor).

## T002 — MOC signature section (L8): blocked, document missing

The algorithm and transforms are already proven: SEFAZ authorized a document signed by the POC (`xmldsig-poc.md`, `homologation.md` run 3). What is missing is the official section reference, which lives in the **main volume of MOC 7.0** (the signature chapter, not Anexo I). That volume is not in `rrf5-private\sefaz\moc`.

**Needed from the operator:** download the current main MOC 7.0 from the Portal NF-e (Documentos → Manuais) into `rrf5-private\sefaz\moc`.

## T003 — `tPag` table and DANFE labels (L5): blocked, current source missing

- The 010g XSD constrains `tPag` only to `[0-9]{2}`, so the code list comes from the MOC.
- The frozen Anexo I (2020) lists 01–05, 10–13, 15–19, 90 and 99. Codes or meanings added by technical notes after 2020 would be missing.
- The table must not be completed from memory.

**Needed from the operator:**
- the current consolidated Anexo I from the Portal NF-e, if a newer edition is published;
- or the technical notes that changed `YA02`/`tPag` after 2020.

Until then, T011 (payment mapping) can be built and tested only for the codes the MVP scenarios use (cash, Pix, credit and debit card) once their current meaning is confirmed in the current source.
