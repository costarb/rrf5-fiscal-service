# Repository audit (T036)

**Status:** PASS — no certificate, private key, CSC, credential or XML with personal
data is tracked, now or in any commit.  
**Executed:** 2026-11-09, branch `feature/001-fiscal-discovery` at `047a979`  
**Scope:** 125 tracked files; full history of every ref (28 commits on `main`,
`develop`, `feature/001-fiscal-discovery`); no stashes.

## Checks

| Check | Method | Result |
| --- | --- | --- |
| Certificate and key files | File names in the tree and in every commit (`git log --all --name-only`) for `.pfx .p12 .pem .key .cer .crt .jks .env*` | PASS — none, ever |
| Secret boundaries | `python scripts/verify_secret_boundaries.py` (also a CI step) | PASS |
| Secret scan | gitleaks 8.30.1 `git` (full history) and `dir` (working tree), with `.gitleaksignore` | PASS — no leaks; the 4 ignored fingerprints are the false positives reviewed in `validation-run.md` |
| Ignore rules | `git check-ignore` on `sefaz/`, `pocs/evidence/private/`, `secrets/`, `*.pfx`, `.env` | PASS — all ignored (`.gitignore` lines 24, 22, 8, 3, 1) |
| XML in history | Every `.xml` ever tracked | Only `pocs/fixtures/golden/nfce-minimal-sp-sn-v1.unsigned.xml` (synthetic, unsigned) and `pocs/java/pom.xml` |
| Personal and fiscal identifiers | Regex scan of tracked files and of every added/removed line in history (SBOMs and lockfiles excluded): formatted CPF/CNPJ, 11- and 14-digit numbers, 44-digit access keys, digit runs of 16 or more, e-mail, phone, CSC/token assignments, private-key headers, password assignments | PASS — every hit classified below; no CPF, e-mail, phone or private-key header found |
| Normative bytes | Official XSD ZIPs and PDFs | Outside Git (`sefaz/` ignored); only hashes and URLs are tracked |

## Classification of identifier hits

Values are abbreviated here on purpose.

| Value | Where | Classification |
| --- | --- | --- |
| CNPJ `00000000000000`, key `3526…84` | Golden fixture, `issuer-synthetic.json`, `danfe-qr.md`, POC guards | Synthetic. The POCs refuse to transmit when the issuer CNPJ is all zeros |
| Keys `2817…24` (2) with CSC `SEU-CODIGO-CSC-CONTRIBUINTE-36-CARACTERES` | `pocs/dotnet/Program.cs` self-test | Public test vectors from the DANFE NFC-e/QR Code manual §4.3.6.1–2; the CSC is the manual's placeholder |
| CPF `00000000191`, CNPJ mask `00.000.000/0000-00`, `nProt` `135000000000000` | `Program.cs`, `SefazClient.cs` | Self-test and mock placeholders |
| `nProt` `1352…07` (17 digits) | `homologation.md`, `danfe-qr.md` | Real protocol from the SEFAZ-SP **homologation** run (`tpAmb` 2, no fiscal validity). Not personal data or a credential; the access key, issuer CNPJ and XML of that document are not in the repository. Kept as evidence |
| `4254…93`, `5671…98` | `validation-run.md`, `normative-baseline.md`, `catalog.json` | GitHub Actions run id; digits inside a SHA-256 |
| `token` | `pocs/contract/src/example.ts` | Read from `FISCAL_API_TOKEN` at runtime; no value in code |

A1 certificate: only its SHA-256 fingerprint (`62cb2f73…`) appears in
`homologation.md`. A fingerprint is public and identifies the certificate
without exposing it. The issuer's identifiers stay in configuration outside Git.

## Reproduction

```text
python scripts/verify_secret_boundaries.py
gitleaks git --redact . ; gitleaks dir --redact .
git log --all --name-only --format= | sort -u
git log --all -p -- . ':(exclude)pocs/evidence/sbom/**' ':(exclude)**/package-lock.json'   # input to the identifier scan
git check-ignore -v sefaz/x.zip pocs/evidence/private/x secrets/x a.pfx .env
```

The identifier scan script is a throwaway one, kept outside the repository. Its
patterns are listed in the table above.
