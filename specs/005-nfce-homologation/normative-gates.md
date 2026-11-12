# Phase 1 — Normative gates (005)

Record of the checklist B items handled in Phase 1. The normative bytes stay outside Git, in `rrf5-private\sefaz`; this file keeps only the hashes and section references.

## Frozen copies used

| Document | Version found | SHA-256 | Location (outside Git) |
| --- | --- | --- | --- |
| MOC 7.0 Anexo I — Leiaute e Regras de Validação da NF-e e da NFC-e | **7.00, November 2020** (cover) | `5eb4cf2010b10b0b62f78197c4eb64025f24535d4c6e61158bc7806dd008f55d` | `sefaz\moc\MOC_Anexo_I_Leiaute.pdf` |
| MOC 7.0 Visão Geral (main volume) | **7.00, November 2020** (cover) | `f664dcf94b77cabb32311620d85a7eb02cdf86adb2d4632ce178af2572dd2ad1` | `sefaz\moc\MOC 7.0 - Visao Geral - NF-e e NFC-e.pdf` (downloaded by the operator on 2026-11-12) |
| Schema package 010g v1.03 | 2026-10-26 | `b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf` (catalog `xsd-nfe-010g-1-03`) | `sefaz\schemas\PL_010g_v1.03.zip` |
| ITI ICP-Brasil bundle | 2026-11-04 | catalog `icp-brasil-ac-bundle` | `icp-brasil\ACcompactado.zip` |

On 2026-11-12 the operator downloaded the Anexo I again. The file is byte-identical to the frozen copy (same SHA-256), so the portal still serves the 2020 edition. The Anexo I copy is the 2020 edition. Rules changed by later technical notes are not in it. It is not used as the current source for anything those notes changed (see T003).

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

## T002 — MOC signature section (L8): done

- **Source:** MOC 7.0 Visão Geral (7.00, November 2020), §4.2.4 "Padrão de Assinatura Digital", Tabela 4-2 "Padrões de Assinatura Digital" (pp. 53–55), and §4.2.5 "Validação de Assinatura Digital pela Secretaria de Fazenda Estadual".
- **Requirements recorded from the official text:**
  - XML Digital Signature, "Enveloped" format; key and certificate from an AC accredited in ICP-Brasil, A1 or A3;
  - `X509Data` with **EndCertOnly** (only the end-user certificate);
  - signature RSA (`xmldsig#rsa-sha1`), digest SHA-1 (`xmldsig#sha1`), Base64;
  - required transforms Enveloped (`xmldsig#enveloped-signature`) and C14N (`REC-xml-c14n-20010315`); `CanonicalizationMethod` C14N;
  - no `X509SubjectName`, `X509IssuerSerial`, `X509IssuerName`, `X509SerialNumber` or `X509SKI`; avoid `KeyValue`/`RSAKeyValue`;
  - the issuer does not supply the revocation list;
  - no namespace prefixes, and the `xmldsig` namespace is declared on the `Signature` element itself (§4.2.5);
  - the signing certificate carries the CNPJ of an establishment of the issuer (OID 2.16.76.1.3.3, §4.2.4.1).
- **Comparison with the POC:** this is exactly the profile the POC signed with and SEFAZ-SP authorized (`homologation.md` run 3), so the L8 limitation is closed. T015 implements this profile and asserts each element listed above.

## T003 — `tPag` table and DANFE labels (L5): blocked, national table needed

**Frozen on 2026-11-12** (operator download, outside Git, `rrf5-private\sefaz\nt`):

| Document | Version | SHA-256 |
| --- | --- | --- |
| Informe Técnico 2024.002 — Atualiza Tabela de Meios de Pagamento | 1.11 (cover: February 2026) | `19953feb5695e728cf15a91be144b7115b3079a31be5ba0fa6eb46af58db1172` |
| Informe Técnico 2026.001 — Tabela de Meios de Pagamento para Vinculação Pgto | 1.01 (August 2026) | `1e58913755c4e617f30619448909dd9ef82b0c772bcd44dc85774e77bcd28900` |

What they establish:

- **IT 2026.001 is not the `tPag` table.** It lists the codes valid for `tpMeioPgto` in the split-payment group `gPgtoVinc` (NT 2026.006): 15, 17, 18, 20, 23 and 24. Any other code is rejected with 1003. The group is exclusive to 010g, so it stays disabled in SP (E4). The IT also says the national table is published on the Portal NF-e and on the DF-e portal of SEFAZ Virtual RS.
- **IT 2024.002 v1.11 records only the changes** to the "Tabela de Meios de Pagamento". The table itself is "disponível no Portal Nacional da NF-e, na aba Documentos, opção Diversos". Changes recorded, with their production dates:

  | IT version | Change | Production |
  | --- | --- | --- |
  | 1.00 | 05 renamed "Cartão da Loja (Private Label)"; 17 becomes "Dinâmico"; adds 20 "Pagamento Instantâneo (PIX) – Estático", 21 "Crédito em Loja", 22 "Pagamento Eletrônico não Informado - falha de hardware do sistema emissor"; notes for 14 and 99; new "Observações" column | 2024-07-01 |
  | 1.01 | text of 05 corrected | — |
  | 1.10 | adds 91 "Pagamento Posterior" | 2025-11-03 |
  | 1.11 | adds 23 "Pagamento Instantâneo (PIX) – Automático" and 24 "TEF – 'Book Transfer'"; changes the description of 18 and the note of 20 | 2026-05-04 |

- **What is still missing:** the full current texts of 05, 18 and 20 are not in the IT. They and the "Observações" column are only in the national table. The table must not be completed from memory or from third-party sites.

**Needed from the operator:** the **Tabela de Meios de Pagamento** itself, from the Portal NF-e, "Documentos → Diversos" (or the DF-e portal of SVRS), downloaded into `rrf5-private\sefaz\nt`.
