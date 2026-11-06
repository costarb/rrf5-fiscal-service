# .NET 10 candidate

Isolated discovery scaffold. It must not be referenced by the future production solution.

Requirements: .NET SDK 10. Run `dotnet run --project pocs/dotnet/Rrf5.Fiscal.Poc.csproj`.

The `validate` command validates an XML against the catalogued official XSD and
refuses a package whose SHA-256 differs from the frozen catalog value:

```powershell
dotnet run -- validate --xml <document.xml> --xsd <schemas>/nfe_v4.00.xsd --package <schemas-010e-v1.02.zip>
```

The `status` command performs a read-only `NFeStatusServico4` probe against
SEFAZ-SP homologation using an A1 stored outside the repository. The password is
read only from `RRF5_A1_PASSWORD`. Evidence: `pocs/evidence/homologation.md`.

```powershell
dotnet run -- status --pfx <path outside the repository>\a1.pfx
```

The `sign` command (T017) signs a synthetic, non-fiscal `infNFe` with the A1
(enveloped XMLDSig, C14N, RSA-SHA1/SHA-1 digest), verifies it, and checks that a
post-signature value change is rejected. Verification fails closed unless there
is exactly one signature, one reference to the single `infNFe` with the expected
`Id`, and the embedded certificate equals the A1. Nothing is transmitted.

```powershell
dotnet run -- sign --pfx <path outside the repository>\a1.pfx [--out <path outside the repository>\signed.xml]
```

The `verify` command checks the XMLDSig of an already-authorized document
(for example, one issued through another ERP with the same A1). It needs only
the embedded public certificate, requires the expected A1 fingerprint, adds a
`vNF` mutation-negative check and compares `protNFe/digVal` with the signed
digest. The XML must live outside the repository; output carries no access key
or issuer data.

```powershell
dotnet run -- verify --xml <path outside the repository>\nfce.xml --cert-sha256 <A1 SHA-256>
```

XML generation remains blocked until a responsible fiscal stakeholder supplies
or approves the synthetic issuer and fiscal profile (CFOP, NCM, CRT and
CST/CSOSN). The POC must not infer those values from the product description.

Pending gates: approved fiscal profile and golden XML generation, XMLDSig with the real A1,
SOAP mock, Linux container, DANFE/QR and dependency review.
