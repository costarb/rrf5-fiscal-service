# Java 25 candidate

Isolated discovery scaffold. It must not become a production dependency by accident.

Requirements: JDK 25 and Maven 3.9+. Run `mvn -f pocs/java/pom.xml package`.

The `validate` command validates an XML against the catalogued official XSD and
refuses a package whose SHA-256 differs from the frozen catalog value:

```text
java com.rrf5.fiscal.poc.Main validate --xml <document.xml> --xsd <schemas>/nfe_v4.00.xsd --package <schemas-010e-v1.02.zip>
```

`sign` and `verify` (T017) use the JDK's built-in XMLDSig (JSR 105) with the
NF-e profile (C14N 1.0, RSA-SHA1, SHA-1, enveloped). Two JDK adjustments are
applied at start-up and documented in `pocs/evidence/xmldsig-poc.md`: the
secure-validation policy minus only the SHA-1 entries, and unwrapped Base64.
The A1 password is read only from `RRF5_A1_PASSWORD`; the A1, signed output and
verified documents must live outside the repository.

```powershell
java -cp target/fiscal-java-poc-0.1.0-SNAPSHOT.jar com.rrf5.fiscal.poc.Main sign --pfx <a1.pfx> [--xml <unsigned.xml> --out <signed.xml>]
java -cp target/fiscal-java-poc-0.1.0-SNAPSHOT.jar com.rrf5.fiscal.poc.Main verify --xml <signed.xml> --cert-sha256 <A1 SHA-256>
```

`generate` and `check-key` mirror the .NET POC and reproduce the golden XML byte
for byte. Runtime jars are copied to `target/lib` at package time:

```powershell
java -cp "target/fiscal-java-poc-0.1.0-SNAPSHOT.jar;target/lib/*" com.rrf5.fiscal.poc.Main generate --case ../fixtures/nfce-minimal.json --profile ../fixtures/fiscal-profile-sp-sn-own-production-v1.json --issuer ../fixtures/issuer-synthetic.json --nnf 1 --cnf 12345678 --dhemi 2026-11-06T12:00:00-03:00 --out <path>
```

Pending gates: SOAP mock and authorization, Linux container, DANFE/QR and dependency review.
