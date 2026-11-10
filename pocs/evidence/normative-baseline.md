# Normative baseline

Snapshot initially checked on **2026-08-04** for the NFC-e model 65 / São Paulo discovery scope. The official portal was rechecked on **2026-08-18** and newer artifacts were found; T008 remains open until their bytes are frozen and hashed.

| Reference | Version | Applicability | Artifact SHA-256 | Status |
| --- | --- | --- | --- | --- |
| MOC | 7.0 | Common NF-e/NFC-e architecture and processing | `f664dcf94b77cabb32311620d85a7eb02cdf86adb2d4632ce178af2572dd2ad1` | Verified from official PDF on 2026-08-18 |
| MOC Annex I | layout 4.00 | XML fields and validation rules | `5eb4cf2010b10b0b62f78197c4eb64025f24535d4c6e61158bc7806dd008f55d` | Verified from official PDF on 2026-08-18 |
| Official XSD package (**current baseline**) | 010g v1.03, published 2026-10-26 | Golden XML validation; NT 2026.006 v1.00 and NT 2026.008 v1.00 | `b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf` | Verified from official ZIP on 2026-11-08; pinned in all POCs |
| Official XSD package | 010f v1.04, published 2026-08-31 | Intermediate; NT 2025.002 v1.50 and NT 2026.007 v1.00 | `b8589490a58a09a993a80e6ac4d7ed10f20892061ecfc56719337098d4b95998` | Verified from official ZIP on 2026-11-08; traceability only |
| Official XSD package (superseded) | 010e v1.02, published 2026-07-10 | Baseline until 2026-11-08; NT 2025.002 v1.40, NT 2026.002 v1.0 and NT 2026.003 v1.0 | `d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1` | Verified from official ZIP on 2026-08-18 |
| NT 2025.002 | 1.51, published 2026-08-04 | RTC fields and validation changes | `a4aaaa181522b43cd90b502f8ccb9e4bdabea30fed838e2d0790be5ba77254a4` | Verified from official PDF on 2026-08-18 |
| DANFE NFC-e / QR Code | 6.0, March 2025 | DANFE rendering and QR Code | `bf906cc212f1edd19b1df7d1cdf4fbf5e73c3fec77567b6e69fa74453c95db5e` | Verified from official PDF on 2026-08-18 |
| NFC-e offline contingency | 2.0 | Fiscal offline workflow for model 65 | `5ff5c8fdc8a55ae91f2f6e7c29566a45ca7e3867c7b76877deb48911071cf06c` | Verified from official PDF on 2026-08-18 |

The authoritative URLs, scope and checked date are stored in `regulatory/catalog.json`. Every hash above is for the downloadable PDF/ZIP bytes, never for the surrounding portal page. Normative bytes remain outside Git pending redistribution review.

The 2026-08-18 recheck invalidated the earlier assumption that NT 2025.002 v1.40 and XSD 010e v1.01 were current. No XML/XSD POC may pin those superseded versions merely because they appear in the initial catalog snapshot.

## 2026-11-07 recheck: 010g v1.03

The operator confirmed from an official source that the XSD package in use is
**010g v1.03** (an intermediate **010f v1.04** also exists). Both supersede
010e v1.02. Only the extracted XSDs were obtained (kept outside Git under
`rrf5-private/sefaz/schemas/`); the official ZIP, its URL, publication date and
included NTs are still needed to freeze the package under the rule above.
Catalog entry `xsd-nfe-010g-1-03` has `hashStatus: PENDING_ARTIFACT`.

Interim per-file SHA-256 (extracted XSDs, not the ZIP):

| File | SHA-256 |
| --- | --- |
| `NFe/nfe_v4.00.xsd` | `adce3646c13ceb54922ec3142fc1dc45bd4fb839ac35ad583e86c733c07d27df` |
| `NFe/leiauteNFe_v4.00.xsd` | `e8470f5d85ded3d99010676087907214aef71c9626140b672abbafa72656c0f5` |
| `NFe/DFeTiposBasicos_v1.00.xsd` | `2da0785a6c70db4f93571187a28514dde715cfd0b0f41e746176fbf3444549f2` |
| `NFe/tiposBasico_v4.00.xsd` | `772619c85723e598840667ca66e7298a250442df47eeb94b397d2a333ce62047` |
| `NFe/xmldsig-core-schema_v1.01.xsd` | `f56744a5f51c03f027de13f39f869307091781a9ef1d91b1ebe14719ce28e1ac` |
| `Evento/e110001_v1.00.xsd` | `3a46fcc6db6c673ae7bbf9767df5fa99d1f4e5b7fbec6bb9575ae358c95b1616` |
| `Evento/e110300_v1.00.xsd` | `3def9bd3e19dfb3e78688afc540c049dc77f6fb11ae2603d85b6e759a7908fa6` |
| `Evento/tiposBasico_v1.03.xsd` | `88bae738184e4af32286a4825392adf7da458dc1fa763e2feca3b90708aa7fff` |

Diagnostic result (not a freeze): the golden NFC-e (SHA-256 `82e3ab6f…`), signed
with a throwaway key, is schema-valid against 010e v1.02, 010f v1.04 and
010g v1.03. 010g adds optional elements (`vUnComLiq`, `vProdLiqTot`, `gPgtoVinc`,
`ICMSPrevistoPagtoAntecip`), makes `IE` optional and restructures the
`gIBSCBS`/`gIBSCBSMono` choice.

**Frozen on 2026-11-08.** The operator supplied `PL_010g_v1.03.zip` and
`PL_010f_v1.04.zip`; both were downloaded again from the official portal
("Versões oficiais (em uso)") and are byte-identical. The ZIP entries equal the
extracted XSDs hashed above. All three POCs now pin the 010g ZIP hash: they
validate the signed golden against it and reject the 010e ZIP with
`XSD_PACKAGE_HASH_MISMATCH`.

No row in this snapshot is an approval of tax treatment. Effective dates and applicability must be reviewed against later technical notes and by the responsible fiscal stakeholder.
