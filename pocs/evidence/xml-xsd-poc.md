# XML/XSD POC evidence

**Status:** IN PROGRESS  
**Checked:** 2026-08-18

## Frozen schema package

- Official package: NF-e/NFC-e 010e v1.02, published 2026-07-10
- Entry schema: `nfe_v4.00.xsd`
- ZIP SHA-256: `d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1`
- Storage: temporary/private evidence location outside Git

Each runtime refuses validation when the supplied ZIP does not match that hash.
The .NET and Java implementations disable external DTD access; TypeScript uses
`xmllint-wasm`, for which every XSD dependency is explicitly preloaded into the
in-memory filesystem.

## Executed checks

| Candidate | Host runtime | Build/typecheck | Invalid XML rejected | Notes |
| --- | --- | --- | --- | --- |
| .NET | 10.0.302 SDK | Pass, zero warnings | Pass (exit 2) | Built-in `XmlSchemaSet` |
| Java | 19.0.2 | Pass with `javac` | Pass (exit 2) | Must be repeated on target Java 25/Maven |
| TypeScript | Node 20.20.2 | Pass | Pass (exit 2) | `xmllint-wasm` 5.2.0, MIT |

The controlled negative test validates `nfe_v4.00.xsd` itself as if it were an
NF-e document. All candidates reject its `xs:schema` root and return sanitized
diagnostics.

## Positive and drift checks with authorized documents — 2026-11-06

**Package re-downloaded** on 2026-11-06 from the official homologation portal
(`hom.nfe.fazenda.gov.br/portal/listaConteudo.aspx?tipoConteudo=BMPFMBoln3w=`,
item "Schemas XML NF-e - 010e_v.1.02", served as `PL_010e_v1.02.zip`). SHA-256
`d44ae5aa…bda1` equals the frozen value; the ZIP stays outside Git. Contents:
`nfe_v4.00.xsd`, `leiauteNFe_v4.00.xsd`, `tiposBasico_v4.00.xsd`,
`DFeTiposBasicos_v1.00.xsd`, `xmldsig-core-schema_v1.01.xsd`.

Input: the `NFe` element (signature included, `protNFe` stripped) of the three
Bling homologation documents described in `homologation.md`, extracted to a
scratch location outside the repository.

| Document (file SHA-256 prefix) | SEFAZ-SP result | .NET | Java (19, `javac`) | TypeScript |
| --- | --- | --- | --- | --- |
| `1dcfe783…` | 100 authorized | valid | valid | valid |
| `a2485370…` | 100 authorized | valid | — | — |
| `eda4a927…` | 225 schema failure | **invalid**: `vUnComLiq` in `prod`, `vProdLiqTot` in `total` | **invalid** (`vUnComLiq`) | **invalid** (`vUnComLiq`) |

Findings:

- First **positive** XSD validation in all three candidates against a real,
  SEFAZ-authorized NFC-e.
- The frozen 010e v1.02 package reproduces SEFAZ-SP homologation's behaviour on
  2026-11-06 for both outcomes: a local XSD gate would have blocked the
  rejected document before transmission.
- `vUnComLiq`, `vProdLiq` and `vProdLiqTot` do not exist anywhere in 010e v1.02;
  they belong to a later layout not yet accepted by SEFAZ-SP homologation. Which
  NT/package introduces them is not established here.
- In 010e v1.02, `det/imposto/IBSCBS` and `total/IBSCBSTot` are `minOccurs="0"`:
  the schema permits omitting them. Whether omission is required or allowed for
  a CRT 1 issuer in 2026 is a business rule (see `fiscal-profile.md`), not
  decided by the schema.

## Blocking input for the positive golden XML

T016 is not complete. XML generation requires an attributable, approved
synthetic fiscal profile containing at least issuer regime and the applicable
CFOP, NCM and CST/CSOSN choices. `nfce-minimal.json` intentionally contains no
such tax assertions. They must not be inferred from its synthetic product.

After approval, add the profile outside sensitive evidence, generate the same
deterministic XML in all candidates, validate it with the frozen XSD, compare
canonical output and record exact results here.
