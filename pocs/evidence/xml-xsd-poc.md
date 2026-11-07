# XML/XSD POC evidence

**Status:** COMPLETE (T016) — .NET, Java 25 and TypeScript generation byte-identical and XSD-valid  
**Checked:** 2026-08-18, updated 2026-11-07

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

## Golden XML generation — .NET (2026-11-06)

**Inputs (all in Git, synthetic):** `pocs/fixtures/nfce-minimal.json`,
`pocs/fixtures/fiscal-profile-sp-sn-own-production-v1.json` (interim
operator-approved profile, see `fiscal-profile.md`) and
`pocs/fixtures/issuer-synthetic.json` (all-zero CNPJ/IE, never transmitted).

**Command:**

```text
dotnet run --project pocs/dotnet -- generate --case pocs/fixtures/nfce-minimal.json   --profile pocs/fixtures/fiscal-profile-sp-sn-own-production-v1.json   --issuer pocs/fixtures/issuer-synthetic.json   --nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00 --out <path>
```

**Golden output:** `pocs/fixtures/golden/nfce-minimal-sp-sn-v1.unsigned.xml`,
2307 bytes, SHA-256 `82e3ab6f83b85555f551d597afad93cba6a9d6557ad03b4ac34e77b255a504b1`.

| Check | Result |
| --- | --- |
| Determinism: two runs, byte comparison | identical |
| No clock/RNG input (`dhEmi`, `cNF`, `nNF` are explicit arguments) | yes |
| Access-key check digit vs. 3 SEFAZ-issued keys (`check-key`) | 3/3 valid; one altered key rejected |
| Unsigned XML against 010e v1.02 | only error: missing `Signature` (expected; schema requires it) |
| Signed with a throwaway self-signed PFX (deleted after), XSD .NET / Java / TypeScript | valid / valid / valid |
| Signature verified, `vNF` mutation rejected | pass / pass |
| Signed output inside the repository refused | pass (`OUTPUT_INSIDE_REPOSITORY`) |

Content decisions encoded (each traceable, none inferred by the code):

- fiscal values (CRT, NCM, CFOP, CSOSN, PIS/COFINS CST/rate, `natOp`) come
  only from the profile file; unsupported groups fail closed;
- no `IBSCBS`/`IBSCBSTot`, no `vItem`/`vNFTot`, no `vTotTrib` (profile flags;
  all `minOccurs="0"` in 010e v1.02);
- homologation first-item `xProd`, `tpImp=4`, `modFrete=9`, no `dest` and
  element order follow the SEFAZ-SP-authorized reference documents;
- `infNFeSupl` uses the QR Code v3 online format observed in those documents
  (`<url>?p=<chave>|3|<tpAmb>`); confirmation against the frozen QR manual/NT
  is still pending;
- `infRespTec` is written only when the issuer file provides it (absent in the
  synthetic issuer).

## Java on the target runtime (2026-11-06)

`mvn -f pocs/java/pom.xml package` with **Temurin 25.0.5 LTS** and **Maven
3.9.16** (`maven.compiler.release=25`): build pass. Validation results:

| Input | Java 25 |
| --- | --- |
| Bling authorized `1dcfe783…` / `a2485370…` | valid / valid |
| Bling rejected (225) `eda4a927…` | invalid: `vUnComLiq` (same cause as SEFAZ) |
| Golden, signed (throwaway PFX, container run) | valid |
| Golden, unsigned | invalid: missing `Signature` (expected) |
| Wrong package file | `XSD_PACKAGE_HASH_MISMATCH` |

All five agree with .NET and TypeScript. Observation: JAXP diagnostics follow
the host locale (pt-BR) and are printed in the console code page, producing
mojibake in JSON output; a production Java service would need a fixed
diagnostic locale and UTF-8 stdout.

## Golden XML generation — Java 25 (2026-11-06)

`pocs/java` `generate` mirrors the .NET contract (same arguments, refusals and
output rules). It writes with StAX (`XMLStreamWriter`) rather than DOM so that
element and attribute order are exactly as listed (the JDK DOM serializer
reorders attributes, see `xmldsig-poc.md`). JSON input via Jackson 2.22.4.

| Check | Result |
| --- | --- |
| Golden inputs, two runs | identical; SHA-256 `82e3ab6f…` = .NET golden, **byte-identical** |
| `check-key` on 3 SEFAZ-issued keys / 1 altered key | valid / rejected |
| Edge fixture (fractional quantities, 10-decimal unit price, banker's rounding of 0.9125 → 0.91, `&` and `<` escaping, UTF-8 accents, change/`vTroco`, `nNF` 987654321, December `dhEmi`) | Java and .NET **byte-identical** (SHA-256 `3a045602…`, 3582 bytes) |
| Unsupported payment method | same `GENERATION_REFUSED` code and message in both |

Rounding parity needed one explicit decision: the .NET POC uses `decimal.Round`
(banker's rounding) for the item total check and away-from-zero for output
formatting; Java mirrors both (`HALF_EVEN` and `HALF_UP`). A production service
must define the rounding rule from the official layout, not from a runtime default.

## T016 closure (2026-11-07)

TypeScript generation is byte-identical to the golden and to the edge fixture
(`typescript-spike.md`). Nothing remains for T016.
