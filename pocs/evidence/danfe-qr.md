# DANFE NFC-e and QR Code capability (T020)

**Status:** PASS for normal (online) emission on the .NET candidate, on synthetic data
and on the real SEFAZ-SP homologation document. The printed QR was validated by
phone scan. Printing on a thermal printer is still to do (see "Not yet proven").
**Checked:** 2026-11-06
**Candidate:** .NET 10 POC (`pocs/dotnet`, commands `danfe` and `danfe-selftest`)

## Normative source

"Manual de Padrões — DANFE NFC-e / QR-Code versões 2.00 e 3.00", version 6.0,
March 2025. This is catalog entry `danfe-nfce-qr` in `regulatory/catalog.json`.
On 2026-11-06 the operator downloaded the PDF again outside the repository. Its
SHA-256 is `bf906cc212f1edd19b1df7d1cdf4fbf5e73c3fec77567b6e69fa74453c95db5e`,
the same value frozen in `normative-baseline.md`. Section numbers below refer to
this manual. The PDF is not committed.

## What the manual settles

| Question | Answer from the manual | Section |
| --- | --- | --- |
| Is a CSC needed for QR v3? | No. For v3 the authenticity check is not a CSC hash. Online emission carries only key, version and environment. Offline contingency carries an RSA-SHA1 signature made with the same certificate that signs the NFC-e. | §4.4 Obs. 1, §4.4.1, §4.4.2 |
| Who may use v3? | A CNPJ issuer may choose v3. The manual points rural producers (individuals) to v3, except in PR. | §4.4 Obs. 2–3 |
| v3 online payload | `<url>?p=<chave>\|3\|<tpAmb>` | §4.4.1 |
| v3 offline payload | `<chave>\|3\|<tpAmb>\|<dd>\|<vNF>\|<tp_idDest>\|<idDest>\|<Base64 RSA-SHA1>`. An empty recipient field keeps only its separator. | §4.4.2 |
| v2 hash | SHA-1 in upper-case hex over the parameters followed by the CSC. In offline mode, the digVal "in HEXA" is the hex of the Base64 **text**, not of the digest bytes. | §4.3.4–4.3.6 |
| QR encoding | ISO/IEC 18004, error correction **M**, character set **UTF-8** | §4, §4.5.2, §4.5.3 |
| QR size | At least 25 mm: 22 mm of content plus 3 mm of margin. Above 25 mm, the margin is 10% of the total size. | §3.1.5, §3.4 |
| Paper | Width at least 56 mm and side margins at least 2 mm. A4 is allowed. Printing on an ECF is never allowed. | §3.3 |
| Content | Only data from the XML plus the authorization answer (protocol, cMsg/xMsg) | §2 |
| Dates | `dhEmi` and `dhRecbto` printed in local time | §3.1.7 |
| Numbers | Comma as decimal separator, dot as thousands separator | §3.1.2, §3.1.3 |
| Homologation | "EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL", centered, in Division VIII | §3.1.8 |

SEFAZ-SP homologation already accepted the v3 online payload in a real
authorization (`homologation.md`, run 3). That closes the CSC question from
T019 for normal emission in SP: with v3, the CSC is not needed. For offline
contingency the manual also needs no CSC under v3, but SP's acceptance of the
v3 offline payload has not been tested, and contingency is out of scope anyway.
Production still needs a decision to adopt v3, recorded in the fiscal
configuration.

## Implementation (.NET)

- `NfceQrCode.cs` builds and parses the v2 and v3 online and offline payloads.
  The v3 offline builder exists only to answer the CSC question. Offline
  contingency stays disabled (`contingency-gates.md`).
- `Danfe.cs` renders a print-ready HTML DANFE with Divisions I–IX and the QR as
  an SVG in millimetres. It uses QRCoder 1.8.1 (MIT) with level M and UTF-8.
  The QR text is taken from `infNFeSupl/qrCode` as authorized and is never
  rebuilt.
- The renderer refuses to print (fails closed) when:
  - there is no `protNFe`, `cStat` ≠ 100, or the protocol belongs to another
    key or environment;
  - the document is not model 65;
  - `tpEmis` ≠ 1;
  - `qrCode` belongs to another key or environment, or does not start with the
    configured UF consultation URL;
  - the paper is narrower than 56 mm or the QR is smaller than 25 mm.
- A real issuer's nfeProc and DANFE must stay outside the repository, and the
  command output does not print the access key.

## Evidence

`dotnet run -- danfe-selftest --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json`
gives **36 passed, 0 failed**. It runs on a synthetic issuer, a throwaway RSA
key and a synthetic protocol:

- the three manual vectors reproduce exactly: v2 online hash
  `DC6AE2C2…6B7511`, offline digVal hex `797a4759…633d` and offline hash
  `4615A93B…5C7D66`;
- the generated XML carries the v3 online payload;
- the v3 offline signature verifies with the signing certificate, without a CSC;
- every required text in Divisions I, II, III, IV, VI, VII and VIII is present;
- a UTC `dhRecbto` is printed in São Paulo local time;
- "Valor a Pagar" is omitted when there are no additions or discount (§3.1.3);
- the 30 mm QR has 27 mm of content, and the 25 mm minimum has exactly 22 mm;
- the nine refusal cases above all refuse.

Round trip: headless Edge rendered the HTML to PNG, and `zxing-cpp` (in a
throwaway venv outside the repository) decoded it as **QR Code, EC level M**.
The text is exactly
`https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode?p=35261100000000000000650010000000011123456784|3|2`.
A v3 online URL encodes as QR version 6 (41 modules). At 30 mm that is
0.659 mm per module.

## Interpretations and gaps (not blocking T020)

1. **QR margin.** "22 mm de conteúdo para 3 mm de margem" adds up to 25 mm only
   if the 3 mm is the sum of both sides. The renderer therefore puts half the
   margin on each side. At the manual's minimum, this is narrower than the ISO
   quiet zone of 4 modules, so `isoQuietZone` is reported but not enforced. The
   blank paper around the QR adds more quiet zone in practice. A phone scan on
   real thermal paper must confirm the default (30 mm).
2. **Payment labels.** Neither the XSD nor this manual lists the `tPag`
   descriptions. The renderer prints `xPag` when present, uses a short label
   table for 01, 02, 03, 04, 17 and 99, and otherwise prints the code. That
   table has not been checked against a frozen MOC copy. Freeze the MOC payment
   table before the foundation phase.
3. **UF-discretion items left off:** `xMsg` in Division VIII, per-item
   additions and discounts, and the summarized ("ecológico") DANFE. Each needs
   SP legislation confirmed before it is enabled.
4. **Offline contingency DANFE** ("EMITIDA EM CONTINGÊNCIA" twice, "Via
   consumidor" and "Via estabelecimento", protocol suppressed) is documented
   above but not rendered. It stays behind `contingency-gates.md`.
5. **Consultation URL.** `--qr-url` is mandatory and comes from configuration,
   per UF and environment (Portal NFC-e, "URL por UF"). It is never hard-coded
   per document.
6. **Container.** The runtime image needs tzdata for `America/Sao_Paulo`. The
   Linux run of `danfe-selftest` is still pending.

## Real authorized document (PASS, 2026-11-06)

`danfe --xml <nfeProc outside repo> --qr-url https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode --out <outside repo>`
ran on the SEFAZ-SP homologation document (series 2, `nNF` 1). Exit code 0;
Divisions I–VIII were rendered (no `infCpl` or `vTotTrib`, so there is no
Division IX). The HTML has SHA-256 `e28116c0…0f51c7` and stays outside Git with
the PNG made from it.

- The issuer CNPJ and address come from the XML.
- The protocol `nProt` is `13526000025378307`, the same as in `homologation.md`
  runs 3 and 4. `dhEmi` and `dhRecbto` are printed in local time.
- QR v3 online, version 6 (41 modules), 30 mm. Decoded from the rendered PNG
  with `zxing-cpp`: one symbol, EC level M, and the text is **identical** to
  `infNFeSupl/qrCode` (`…/qrcode?p=<chave>|3|2`).

## Printed DANFE and phone scan (PASS, 2026-11-06)

The operator printed the real document's DANFE on a common (non-thermal)
printer. A phone scan of the printed 30 mm QR opened the SEFAZ-SP homologation
consultation for that document.

## Not yet proven

- printing on an 80 mm thermal printer and on a 58 mm one (the minimum is
  56 mm), followed by a phone scan of the thermal print.
