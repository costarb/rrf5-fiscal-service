# Normative baseline

Snapshot initially checked on **2026-08-04** for the NFC-e model 65 / São Paulo discovery scope. The official portal was rechecked on **2026-08-18** and newer artifacts were found; T008 remains open until their bytes are frozen and hashed.

| Reference | Version | Applicability | Artifact SHA-256 | Status |
| --- | --- | --- | --- | --- |
| MOC | 7.0 | Common NF-e/NFC-e architecture and processing | `f664dcf94b77cabb32311620d85a7eb02cdf86adb2d4632ce178af2572dd2ad1` | Verified from official PDF on 2026-08-18 |
| MOC Annex I | layout 4.00 | XML fields and validation rules | `5eb4cf2010b10b0b62f78197c4eb64025f24535d4c6e61158bc7806dd008f55d` | Verified from official PDF on 2026-08-18 |
| Official XSD package | 010e v1.02, published 2026-07-10 | Golden XML validation; includes NT 2025.002 v1.40, NT 2026.002 v1.0 and NT 2026.003 v1.0 | `d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1` | Verified from official ZIP on 2026-08-18 |
| NT 2025.002 | 1.51, published 2026-08-04 | RTC fields and validation changes | `a4aaaa181522b43cd90b502f8ccb9e4bdabea30fed838e2d0790be5ba77254a4` | Verified from official PDF on 2026-08-18 |
| DANFE NFC-e / QR Code | 6.0, March 2025 | DANFE rendering and QR Code | `bf906cc212f1edd19b1df7d1cdf4fbf5e73c3fec77567b6e69fa74453c95db5e` | Verified from official PDF on 2026-08-18 |
| NFC-e offline contingency | 2.0 | Fiscal offline workflow for model 65 | `5ff5c8fdc8a55ae91f2f6e7c29566a45ca7e3867c7b76877deb48911071cf06c` | Verified from official PDF on 2026-08-18 |

The authoritative URLs, scope and checked date are stored in `regulatory/catalog.json`. Every hash above is for the downloadable PDF/ZIP bytes, never for the surrounding portal page. Normative bytes remain outside Git pending redistribution review.

The 2026-08-18 recheck invalidated the earlier assumption that NT 2025.002 v1.40 and XSD 010e v1.01 were current. No XML/XSD POC may pin those superseded versions merely because they appear in the initial catalog snapshot.

No row in this snapshot is an approval of tax treatment. Effective dates and applicability must be reviewed against later technical notes and by the responsible fiscal stakeholder.
