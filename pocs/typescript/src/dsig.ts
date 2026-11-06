// T017 spike (TypeScript): enveloped XMLDSig with the NF-e profile using
// xml-crypto 6.4.0 (C14N, RSA-SHA1, SHA-1, enveloped + C14N transforms,
// X509Certificate in KeyInfo). Node cannot open PKCS#12 for signing keys, so
// node-forge 1.4.3 extracts the key and certificate from the PFX in memory.
import { createHash, X509Certificate } from "node:crypto";
import { DOMParser } from "@xmldom/xmldom";
import forge from "node-forge";
import { SignedXml } from "xml-crypto";

const NFE_NS = "http://www.portalfiscal.inf.br/nfe";
const DSIG_NS = "http://www.w3.org/2000/09/xmldsig#";
const C14N = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
const RSA_SHA1 = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
const SHA1 = "http://www.w3.org/2000/09/xmldsig#sha1";
const ENVELOPED = "http://www.w3.org/2000/09/xmldsig#enveloped-signature";

export interface A1 {
  privateKeyPem: string;
  certificatePem: string;
  certificateDer: Buffer;
  sha256: string;
  notBefore: Date;
  notAfter: Date;
}

export function loadA1(pfx: Buffer, password: string): A1 {
  const p12 = forge.pkcs12.pkcs12FromAsn1(forge.asn1.fromDer(forge.util.createBuffer(pfx.toString("binary"))), password);
  const shrouded = forge.pki.oids.pkcs8ShroudedKeyBag as string;
  const plain = forge.pki.oids.keyBag as string;
  const certBag = forge.pki.oids.certBag as string;
  const keys = [...(p12.getBags({ bagType: shrouded })[shrouded] ?? []), ...(p12.getBags({ bagType: plain })[plain] ?? [])];
  const key = keys.find((bag) => bag.key)?.key as forge.pki.rsa.PrivateKey | undefined;
  if (!key) throw new Error("A1_NO_PRIVATE_KEY");
  // The leaf is the certificate whose public modulus matches the private key.
  const certificates = (p12.getBags({ bagType: certBag })[certBag] ?? [])
    .map((bag: forge.pkcs12.Bag) => bag.cert)
    .filter((cert: forge.pki.Certificate | undefined): cert is forge.pki.Certificate => Boolean(cert));
  const leaf = certificates.find((cert: forge.pki.Certificate) => (cert.publicKey as forge.pki.rsa.PublicKey).n.equals(key.n));
  if (!leaf) throw new Error("A1_NO_MATCHING_CERTIFICATE");
  const certificateDer = Buffer.from(forge.asn1.toDer(forge.pki.certificateToAsn1(leaf)).getBytes(), "binary");
  return {
    privateKeyPem: forge.pki.privateKeyToPem(key),
    certificatePem: forge.pki.certificateToPem(leaf),
    certificateDer,
    sha256: createHash("sha256").update(certificateDer).digest("hex"),
    notBefore: leaf.validity.notBefore,
    notAfter: leaf.validity.notAfter,
  };
}

function parse(xml: string) {
  // xmldom reports malformed input via onError; treat any error as fatal.
  return new DOMParser({ onError: (level, message) => { if (level !== "warning") throw new Error(message); } })
    .parseFromString(xml, "text/xml");
}

export function sign(xml: string, a1: A1): string {
  const document = parse(xml);
  const infNfe = document.getElementsByTagNameNS(NFE_NS, "infNFe");
  if (document.documentElement?.localName !== "NFe" || infNfe.length !== 1
    || document.getElementsByTagNameNS(DSIG_NS, "Signature").length !== 0) {
    throw new Error("UNEXPECTED_STRUCTURE: expected one NFe/infNFe and no existing Signature.");
  }
  const signer = new SignedXml({
    privateKey: a1.privateKeyPem,
    publicCert: a1.certificatePem,
    canonicalizationAlgorithm: C14N,
    signatureAlgorithm: RSA_SHA1,
  });
  signer.addReference({
    xpath: "/*[local-name(.)='NFe']/*[local-name(.)='infNFe']",
    digestAlgorithm: SHA1,
    transforms: [ENVELOPED, C14N],
  });
  signer.computeSignature(xml, { location: { reference: "/*[local-name(.)='NFe']", action: "append" } });
  return signer.getSignedXml();
}

export interface Verification {
  verified: boolean;
  detail: string;
}

// Fail closed: exactly one Signature and one infNFe, a single Reference whose URI
// targets that infNFe's Id, an embedded certificate byte-identical to the expected
// one, then the cryptographic check by xml-crypto.
export function verify(xml: string, expectedCertificateDer: Buffer): Verification {
  try {
    const document = parse(xml);
    const signatures = document.getElementsByTagNameNS(DSIG_NS, "Signature");
    const infNfe = document.getElementsByTagNameNS(NFE_NS, "infNFe");
    if (signatures.length !== 1 || infNfe.length !== 1) return { verified: false, detail: "structure" };
    const signature = signatures.item(0)!;
    const id = infNfe.item(0)!.getAttribute("Id") ?? "";
    const references = signature.getElementsByTagNameNS(DSIG_NS, "Reference");
    if (references.length !== 1 || references.item(0)!.getAttribute("URI") !== `#${id}`) return { verified: false, detail: "reference" };
    const certificates = signature.getElementsByTagNameNS(DSIG_NS, "X509Certificate");
    if (certificates.length !== 1) return { verified: false, detail: "certificate" };
    const embedded = Buffer.from((certificates.item(0)!.textContent ?? "").replace(/\s+/g, ""), "base64");
    if (!embedded.equals(expectedCertificateDer)) return { verified: false, detail: "certificate" };

    const pem = new X509Certificate(embedded).toString();
    const verifier = new SignedXml({ publicCert: pem });
    verifier.loadSignature(signature.toString());
    const valid = verifier.checkSignature(xml);
    return { verified: valid, detail: valid ? "ok" : "cryptographic" };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    // xml-crypto throws on digest/signature mismatch; classify those as cryptographic.
    return { verified: false, detail: /invalid signature|digest|signature value/i.test(message) ? "cryptographic" : `error: ${message}` };
  }
}

export function embeddedCertificate(xml: string): Buffer {
  const nodes = parse(xml).getElementsByTagNameNS(DSIG_NS, "X509Certificate");
  if (nodes.length !== 1) throw new Error("Expected exactly one embedded X509Certificate.");
  return Buffer.from((nodes.item(0)!.textContent ?? "").replace(/\s+/g, ""), "base64");
}

export function mutateTotal(xml: string, documentMode: boolean): string {
  const pattern = documentMode ? /(<ICMSTot>(?:(?!<\/ICMSTot>).)*?<vNF>)([0-9.]+)(<\/vNF>)/s : /(<vTest>)([0-9.]+)(<\/vTest>)/;
  return xml.replace(pattern, (_m, a: string, value: string, b: string) => `${a}${value === "10.01" ? "10.02" : "10.01"}${b}`);
}

export const algorithms = { C14N, RSA_SHA1, SHA1 };
