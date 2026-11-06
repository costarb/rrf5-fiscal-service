# Java 25 candidate

Isolated discovery scaffold. It must not become a production dependency by accident.

Requirements: JDK 25 and Maven 3.9+. Run `mvn -f pocs/java/pom.xml package`.

The `validate` command validates an XML against the catalogued official XSD and
refuses a package whose SHA-256 differs from the frozen catalog value:

```text
java com.rrf5.fiscal.poc.Main validate --xml <document.xml> --xsd <schemas>/nfe_v4.00.xsd --package <schemas-010e-v1.02.zip>
```

XML generation remains blocked until a responsible fiscal stakeholder supplies
or approves the synthetic issuer and fiscal profile (CFOP, NCM, CRT and
CST/CSOSN). The POC must not infer those values from the product description.

Pending gates: execution on Java 25 with Maven, approved fiscal profile and
golden XML generation, XMLDSig/A1, SOAP mock, Linux container, DANFE/QR and
dependency review.
