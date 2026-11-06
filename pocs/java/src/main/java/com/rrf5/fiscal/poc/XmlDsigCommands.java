package com.rrf5.fiscal.poc;

import java.io.ByteArrayInputStream;
import java.io.StringWriter;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.PrivateKey;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collections;
import java.util.Date;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import javax.xml.XMLConstants;
import javax.xml.crypto.dsig.CanonicalizationMethod;
import javax.xml.crypto.dsig.DigestMethod;
import javax.xml.crypto.dsig.Reference;
import javax.xml.crypto.dsig.SignatureMethod;
import javax.xml.crypto.dsig.SignedInfo;
import javax.xml.crypto.dsig.Transform;
import javax.xml.crypto.dsig.XMLSignature;
import javax.xml.crypto.dsig.XMLSignatureFactory;
import javax.xml.crypto.dsig.dom.DOMSignContext;
import javax.xml.crypto.dsig.dom.DOMValidateContext;
import javax.xml.crypto.dsig.keyinfo.KeyInfo;
import javax.xml.crypto.dsig.keyinfo.KeyInfoFactory;
import javax.xml.crypto.dsig.keyinfo.X509Data;
import javax.xml.crypto.dsig.spec.C14NMethodParameterSpec;
import javax.xml.crypto.dsig.spec.TransformParameterSpec;
import javax.xml.parsers.DocumentBuilderFactory;
import javax.xml.transform.OutputKeys;
import javax.xml.transform.TransformerFactory;
import javax.xml.transform.dom.DOMSource;
import javax.xml.transform.stream.StreamResult;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;

// T017 (Java 25): enveloped XMLDSig for NF-e using only the JDK (JSR 105),
// mirroring the .NET POC: C14N 1.0, RSA-SHA1, SHA-1 digest, enveloped + C14N
// transforms, X509Certificate in KeyInfo. Verification fails closed unless there is
// exactly one Signature with one Reference to the single infNFe carrying the
// expected Id, and the embedded certificate matches the expected one.
final class XmlDsigCommands {
    static final String NFE_NS = "http://www.portalfiscal.inf.br/nfe";
    static final String DSIG_NS = XMLSignature.XMLNS;
    private static final String SYNTHETIC_ID = "NFe00000000000000000000000000000000000000000000";

    private XmlDsigCommands() {}

    // JDK 25 secure validation forbids SHA-1 digests and RSA-SHA1 signatures, which
    // the NF-e layout mandates. Remove exactly those two entries and keep every other
    // protection (transform/reference limits, no external URIs, minimum key sizes,
    // no duplicate Ids). Must run before the XMLDSig provider reads the policy.
    static final List<String> NFE_REQUIRED_ALGORITHMS = List.of(
            "disallowAlg http://www.w3.org/2000/09/xmldsig#sha1",
            "disallowAlg http://www.w3.org/2000/09/xmldsig#rsa-sha1");

    static String relaxPolicyForNfe() {
        String property = "jdk.xml.dsig.secureValidationPolicy";
        String current = java.security.Security.getProperty(property);
        if (current == null) {
            Main.fail("DSIG_POLICY_MISSING", property + " is not defined; refusing to run without secure validation policy.");
        }
        List<String> kept = Arrays.stream(current.split(",")).map(String::trim)
                .filter(entry -> !entry.isEmpty() && !NFE_REQUIRED_ALGORITHMS.contains(entry)).toList();
        java.security.Security.setProperty(property, String.join(",", kept));
        // The JDK wraps Base64 in SignatureValue/X509Certificate every 76 chars with
        // CR entities and line feeds; documents authorized by SEFAZ carry unwrapped values.
        System.setProperty("com.sun.org.apache.xml.internal.security.ignoreLineBreaks", "true");
        return "jdk-default-minus-sha1-and-rsa-sha1 (" + kept.size() + " rules kept)";
    }

    record Verification(boolean verified, String detail) {}

    static void sign(Map<String, String> options) throws Exception {
        Path pfx = Main.requiredFile(options, "--pfx");
        if (Main.insideRepository(pfx)) {
            Main.fail("A1_INSIDE_REPOSITORY", "The A1 file must live outside the repository working tree.");
        }
        String password = System.getenv("RRF5_A1_PASSWORD");
        if (password == null || password.isEmpty()) {
            Main.fail("A1_PASSWORD_MISSING", "Set RRF5_A1_PASSWORD; the password is never accepted as an argument.");
        }

        KeyStore store = KeyStore.getInstance("PKCS12");
        try (var input = Files.newInputStream(pfx)) {
            store.load(input, password.toCharArray());
        } catch (Exception exception) {
            Main.fail("A1_LOAD_FAILED", "The PKCS#12 file could not be opened with the supplied password.");
        }
        String alias = Collections.list(store.aliases()).stream().filter(a -> {
            try { return store.isKeyEntry(a); } catch (Exception e) { return false; }
        }).findFirst().orElse(null);
        if (alias == null) {
            Main.fail("A1_NO_PRIVATE_KEY", "The PKCS#12 file does not contain a private key.");
        }
        PrivateKey key = (PrivateKey) store.getKey(alias, password.toCharArray());
        X509Certificate certificate = (X509Certificate) store.getCertificate(alias);
        if (!"RSA".equals(key.getAlgorithm())) {
            Main.fail("A1_NOT_RSA", "The A1 private key is not RSA.");
        }
        certificate.checkValidity(new Date());

        boolean documentMode = options.containsKey("--xml");
        Document document;
        if (documentMode) {
            if (!options.containsKey("--out")) {
                Main.fail("OUT_REQUIRED", "Signing a document requires --out outside the repository.");
            }
            document = parse(Files.readAllBytes(Main.requiredFile(options, "--xml")));
        } else {
            document = parse(("<NFe xmlns=\"" + NFE_NS + "\"><infNFe Id=\"" + SYNTHETIC_ID + "\" versao=\"4.00\">"
                    + "<poc>SYNTHETIC NON-FISCAL XMLDSIG TEST - NO TAX CONTENT</poc><vTest>10.00</vTest>"
                    + "</infNFe></NFe>").getBytes(StandardCharsets.UTF_8));
        }

        Element root = document.getDocumentElement();
        NodeList infNfe = document.getElementsByTagNameNS(NFE_NS, "infNFe");
        if (!"NFe".equals(root.getLocalName()) || infNfe.getLength() != 1
                || document.getElementsByTagNameNS(DSIG_NS, "Signature").getLength() != 0) {
            Main.fail("UNEXPECTED_STRUCTURE", "Expected one NFe/infNFe and no existing Signature.");
        }
        Element target = (Element) infNfe.item(0);
        String id = target.getAttribute("Id");
        target.setIdAttribute("Id", true);

        XMLSignatureFactory factory = XMLSignatureFactory.getInstance("DOM");
        Reference reference = factory.newReference("#" + id,
                factory.newDigestMethod(DigestMethod.SHA1, null),
                List.of(factory.newTransform(Transform.ENVELOPED, (TransformParameterSpec) null),
                        factory.newTransform(CanonicalizationMethod.INCLUSIVE, (TransformParameterSpec) null)),
                null, null);
        SignedInfo signedInfo = factory.newSignedInfo(
                factory.newCanonicalizationMethod(CanonicalizationMethod.INCLUSIVE, (C14NMethodParameterSpec) null),
                factory.newSignatureMethod(SignatureMethod.RSA_SHA1, null),
                List.of(reference));
        KeyInfoFactory keyInfoFactory = factory.getKeyInfoFactory();
        X509Data x509 = keyInfoFactory.newX509Data(List.of(certificate));
        KeyInfo keyInfo = keyInfoFactory.newKeyInfo(List.of(x509));
        factory.newXMLSignature(signedInfo, keyInfo).sign(new DOMSignContext(key, root));

        String signedText = serialize(document);
        Verification verified = verify(signedText, id, certificate);
        String mutated = mutate(signedText, documentMode);
        Verification mutation = verify(mutated, id, certificate);
        boolean mutationRejected = !mutation.verified() && "cryptographic".equals(mutation.detail());

        if (options.containsKey("--out")) {
            Path out = Path.of(options.get("--out")).toAbsolutePath().normalize();
            if (Main.insideRepository(out)) {
                Main.fail("OUTPUT_INSIDE_REPOSITORY", "Signed XML embeds the A1 public certificate; write it outside the repository.");
            }
            Files.writeString(out, signedText, StandardCharsets.UTF_8);
        }

        System.out.printf("{\"candidate\":\"java\",\"command\":\"sign\",\"runtime\":\"%s\",\"scope\":\"%s\","
                        + "\"signatureMethod\":\"%s\",\"digestMethod\":\"%s\",\"canonicalization\":\"%s\","
                        + "\"certificateSha256\":\"%s\",\"signatureVerified\":%b,\"verificationDetail\":\"%s\","
                        + "\"mutationRejected\":%b}%n",
                Runtime.version(), documentMode ? "generated-nfe-document" : "synthetic-non-fiscal-xml",
                SignatureMethod.RSA_SHA1, DigestMethod.SHA1, CanonicalizationMethod.INCLUSIVE,
                sha256(certificate.getEncoded()), verified.verified(), Main.jsonEscape(verified.detail()), mutationRejected);
        if (!verified.verified() || !mutationRejected) {
            System.exit(2);
        }
    }

    static void verifyCommand(Map<String, String> options) throws Exception {
        Path xml = Main.requiredFile(options, "--xml");
        if (Main.insideRepository(xml)) {
            Main.fail("XML_INSIDE_REPOSITORY", "Fiscal XML carries taxpayer data; keep it outside the repository.");
        }
        String expected = options.get("--cert-sha256");
        if (expected == null || expected.length() != 64) {
            Main.fail("FINGERPRINT_REQUIRED", "--cert-sha256 must be the expected 64-hex A1 SHA-256 fingerprint.");
        }
        String text = Files.readString(xml, StandardCharsets.UTF_8);
        Document document = parse(text.getBytes(StandardCharsets.UTF_8));
        NodeList infNfe = document.getElementsByTagNameNS(NFE_NS, "infNFe");
        NodeList certificates = document.getElementsByTagNameNS(DSIG_NS, "X509Certificate");
        if (infNfe.getLength() != 1 || certificates.getLength() != 1) {
            Main.fail("UNEXPECTED_STRUCTURE", "Expected exactly one infNFe and one embedded X509Certificate.");
        }
        String id = ((Element) infNfe.item(0)).getAttribute("Id");
        X509Certificate embedded = (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(
                new ByteArrayInputStream(Base64.getMimeDecoder().decode(certificates.item(0).getTextContent().trim())));
        boolean fingerprintMatches = sha256(embedded.getEncoded()).equals(expected.toLowerCase());

        Verification verified = verify(text, id, embedded);
        Verification mutation = verify(mutate(text, true), id, embedded);
        boolean mutationRejected = !mutation.verified() && "cryptographic".equals(mutation.detail());

        String digest = firstText(document, DSIG_NS, "DigestValue");
        String protocolDigest = firstText(document, NFE_NS, "digVal");

        System.out.printf("{\"candidate\":\"java\",\"command\":\"verify\",\"runtime\":\"%s\",\"fileSha256\":\"%s\","
                        + "\"certificateSha256Matches\":%b,\"signatureVerified\":%b,\"verificationDetail\":\"%s\","
                        + "\"mutationRejected\":%b,\"protocolDigestMatches\":%b,\"cStat\":%s}%n",
                Runtime.version(), sha256(Files.readAllBytes(xml)), fingerprintMatches, verified.verified(),
                Main.jsonEscape(verified.detail()), mutationRejected,
                digest != null && digest.equals(protocolDigest), quoted(protocolStatus(document)));
        if (!fingerprintMatches || !verified.verified() || !mutationRejected) {
            System.exit(2);
        }
    }

    // Fail-closed structural checks, then cryptographic validation of the
    // reference digest and the SignatureValue.
    static Verification verify(String xml, String expectedId, X509Certificate expectedCertificate) {
        try {
            Document document = parse(xml.getBytes(StandardCharsets.UTF_8));
            NodeList signatures = document.getElementsByTagNameNS(DSIG_NS, "Signature");
            NodeList infNfe = document.getElementsByTagNameNS(NFE_NS, "infNFe");
            if (signatures.getLength() != 1 || infNfe.getLength() != 1
                    || !expectedId.equals(((Element) infNfe.item(0)).getAttribute("Id"))) {
                return new Verification(false, "structure");
            }
            ((Element) infNfe.item(0)).setIdAttribute("Id", true);

            DOMValidateContext context = new DOMValidateContext(expectedCertificate.getPublicKey(), signatures.item(0));
            XMLSignature signature = XMLSignatureFactory.getInstance("DOM").unmarshalXMLSignature(context);
            List<?> references = signature.getSignedInfo().getReferences();
            if (references.size() != 1 || !("#" + expectedId).equals(((Reference) references.get(0)).getURI())) {
                return new Verification(false, "reference");
            }
            List<?> keyContent = signature.getKeyInfo().getContent();
            boolean certificateMatches = keyContent.size() == 1 && keyContent.get(0) instanceof X509Data data
                    && data.getContent().size() == 1 && data.getContent().get(0) instanceof X509Certificate embedded
                    && Arrays.equals(embedded.getEncoded(), expectedCertificate.getEncoded());
            if (!certificateMatches) {
                return new Verification(false, "certificate");
            }
            boolean valid = signature.validate(context);
            return new Verification(valid, valid ? "ok" : "cryptographic");
        } catch (Exception exception) {
            return new Verification(false, exception.getClass().getSimpleName() + ": " + exception.getMessage());
        }
    }

    private static String mutate(String xml, boolean documentMode) throws Exception {
        Document document = parse(xml.getBytes(StandardCharsets.UTF_8));
        Node node = documentMode
                ? ((Element) document.getElementsByTagNameNS(NFE_NS, "ICMSTot").item(0)).getElementsByTagNameNS(NFE_NS, "vNF").item(0)
                : document.getElementsByTagNameNS(NFE_NS, "vTest").item(0);
        node.setTextContent(new BigDecimal(node.getTextContent()).add(new BigDecimal("0.01")).toPlainString());
        return serialize(document);
    }

    static Document parse(byte[] bytes) throws Exception {
        DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
        factory.setNamespaceAware(true);
        factory.setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true);
        factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
        factory.setXIncludeAware(false);
        factory.setExpandEntityReferences(false);
        return factory.newDocumentBuilder().parse(new ByteArrayInputStream(bytes));
    }

    static String serialize(Document document) throws Exception {
        document.setXmlStandalone(true);
        var transformer = TransformerFactory.newInstance().newTransformer();
        transformer.setOutputProperty(OutputKeys.OMIT_XML_DECLARATION, "no");
        transformer.setOutputProperty(OutputKeys.ENCODING, "UTF-8");
        transformer.setOutputProperty(OutputKeys.INDENT, "no");
        StringWriter writer = new StringWriter();
        transformer.transform(new DOMSource(document), new StreamResult(writer));
        return writer.toString();
    }

    static String sha256(byte[] bytes) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }

    private static String firstText(Document document, String ns, String name) {
        NodeList nodes = document.getElementsByTagNameNS(ns, name);
        return nodes.getLength() == 0 ? null : nodes.item(0).getTextContent();
    }

    private static String protocolStatus(Document document) {
        NodeList infProt = document.getElementsByTagNameNS(NFE_NS, "infProt");
        if (infProt.getLength() == 0) {
            return null;
        }
        NodeList cStat = ((Element) infProt.item(0)).getElementsByTagNameNS(NFE_NS, "cStat");
        return cStat.getLength() == 0 ? null : cStat.item(0).getTextContent();
    }

    private static String quoted(String value) {
        return value == null ? "null" : "\"" + Main.jsonEscape(value) + "\"";
    }
}
