package com.rrf5.fiscal.poc;

import com.fasterxml.jackson.databind.JsonNode;
import java.io.ByteArrayOutputStream;
import java.math.BigDecimal;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.time.OffsetDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;
import javax.xml.stream.XMLOutputFactory;
import javax.xml.stream.XMLStreamWriter;

// T016 (Java 25): deterministic NFC-e 4.00 generation, mirroring the .NET POC so
// both produce the same bytes. StAX is used instead of DOM so element and
// attribute order are written exactly as listed. No tax decision is made here:
// every fiscal value comes from the profile file; unsupported groups fail closed.
final class NfceGenerator {
    static final String NFE_NS = "http://www.portalfiscal.inf.br/nfe";
    private static final String HOMOLOGATION_FIRST_ITEM = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";
    private static final DateTimeFormatter DH_EMI = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ssxxx");
    private static final DateTimeFormatter YYMM = DateTimeFormatter.ofPattern("yyMM");

    private NfceGenerator() {}

    record Result(byte[] xml, String accessKey) {}

    static final class RefusedException extends Exception {
        RefusedException(String message) { super(message); }
    }

    static Result generate(JsonNode fixture, JsonNode profile, JsonNode issuer, int nNF, String cNF, OffsetDateTime dhEmi)
            throws Exception {
        require("NFCE".equals(str(fixture, "documentType")), "Fixture documentType must be NFCE.");
        require("HOMOLOGATION".equals(str(fixture, "environment")), "Only HOMOLOGATION is supported by the POC.");
        require("NFCE_65".equals(str(profile, "documentType")) && "SP".equals(str(profile, "uf")), "Profile must be NFCE_65/SP.");
        require(!profile.path("ibsCbs").path("send").asBoolean(true), "Profile requests IBS/CBS groups; not implemented.");
        require(!profile.path("vTotTrib").path("send").asBoolean(true), "Profile requests vTotTrib; source not implemented.");
        require(cNF.length() == 8 && cNF.chars().allMatch(c -> c >= '0' && c <= '9'), "cNF must be 8 digits.");
        require(nNF > 0 && nNF <= 999_999_999, "nNF out of range.");

        String profileId = str(profile, "profileId");
        JsonNode item0 = profile.path("item");
        JsonNode address = issuer.path("address");
        String cnpj = str(issuer, "cnpj");
        int series = Integer.parseInt(str(issuer, "series"));
        final String cUF = "35", mod = "65", tpEmis = "1", tpAmb = "2";

        String keyBody = cUF + dhEmi.format(YYMM) + cnpj + mod + String.format("%03d", series)
                + String.format("%09d", nNF) + tpEmis + cNF;
        require(keyBody.length() == 43 && keyBody.chars().allMatch(c -> c >= '0' && c <= '9'), "Access key components are malformed.");
        int cDV = accessKeyCheckDigit(keyBody);
        String accessKey = keyBody + cDV;

        List<JsonNode> items = new ArrayList<>();
        fixture.path("items").forEach(items::add);
        require(!items.isEmpty(), "Fixture has no items.");

        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        XMLStreamWriter w = XMLOutputFactory.newFactory().createXMLStreamWriter(buffer, "UTF-8");
        w.writeStartDocument("utf-8", "1.0");
        w.writeStartElement("NFe");
        w.writeDefaultNamespace(NFE_NS);
        w.writeStartElement("infNFe");
        w.writeAttribute("versao", "4.00");
        w.writeAttribute("Id", "NFe" + accessKey);

        w.writeStartElement("ide");
        e(w, "cUF", cUF); e(w, "cNF", cNF); e(w, "natOp", str(profile, "natOp")); e(w, "mod", mod);
        e(w, "serie", Integer.toString(series)); e(w, "nNF", Integer.toString(nNF));
        e(w, "dhEmi", dhEmi.format(DH_EMI));
        e(w, "tpNF", "1"); e(w, "idDest", "1"); e(w, "cMunFG", str(address, "cityCode"));
        e(w, "tpImp", "4"); e(w, "tpEmis", tpEmis); e(w, "cDV", Integer.toString(cDV)); e(w, "tpAmb", tpAmb);
        e(w, "finNFe", "1"); e(w, "indFinal", "1"); e(w, "indPres", "1"); e(w, "procEmi", "0");
        e(w, "verProc", "RRF5-POC-0.1");
        w.writeEndElement();

        w.writeStartElement("emit");
        e(w, "CNPJ", cnpj); e(w, "xNome", str(issuer, "name"));
        w.writeStartElement("enderEmit");
        e(w, "xLgr", str(address, "street")); e(w, "nro", str(address, "number"));
        e(w, "xBairro", str(address, "district")); e(w, "cMun", str(address, "cityCode"));
        e(w, "xMun", str(address, "city")); e(w, "UF", str(address, "uf")); e(w, "CEP", str(address, "zip"));
        e(w, "cPais", "1058"); e(w, "xPais", "Brasil");
        w.writeEndElement();
        e(w, "IE", str(issuer, "ie")); e(w, "CRT", str(profile, "crt"));
        w.writeEndElement();

        BigDecimal vProd = BigDecimal.ZERO;
        for (int index = 0; index < items.size(); index++) {
            JsonNode item = items.get(index);
            require(profileId.equals(str(item, "fiscalProfileId")), "Item " + (index + 1) + " references a different fiscal profile.");
            BigDecimal qty = dec(item, "quantity");
            BigDecimal unit = dec(item, "unitPrice");
            BigDecimal total = dec(item, "totalPrice");
            // .NET decimal.Round default is banker's rounding; mirror it.
            require(qty.multiply(unit).setScale(2, RoundingMode.HALF_EVEN).compareTo(total) == 0,
                    "Item " + (index + 1) + ": quantity x unitPrice != totalPrice.");
            vProd = vProd.add(total);

            w.writeStartElement("det");
            w.writeAttribute("nItem", Integer.toString(index + 1));
            w.writeStartElement("prod");
            e(w, "cProd", str(item, "productId")); e(w, "cEAN", str(item0, "gtin"));
            e(w, "xProd", index == 0 ? HOMOLOGATION_FIRST_ITEM : str(item, "description"));
            e(w, "NCM", str(item0, "ncm")); e(w, "CFOP", str(item0, "cfop"));
            e(w, "uCom", str(item0, "unit")); e(w, "qCom", fixed(qty, 4));
            e(w, "vUnCom", flexible(unit)); e(w, "vProd", money(total));
            e(w, "cEANTrib", str(item0, "gtin")); e(w, "uTrib", str(item0, "unit"));
            e(w, "qTrib", fixed(qty, 4)); e(w, "vUnTrib", flexible(unit));
            e(w, "indTot", "1");
            w.writeEndElement();

            w.writeStartElement("imposto");
            JsonNode icms = item0.path("icms");
            require("ICMSSN102".equals(str(icms, "group")), "Only ICMSSN102 is implemented.");
            w.writeStartElement("ICMS"); w.writeStartElement("ICMSSN102");
            e(w, "orig", str(icms, "orig")); e(w, "CSOSN", str(icms, "csosn"));
            w.writeEndElement(); w.writeEndElement();
            writeOutr(w, "PIS", item0.path("pis"), "pPIS", "vPIS");
            writeOutr(w, "COFINS", item0.path("cofins"), "pCOFINS", "vCOFINS");
            w.writeEndElement();
            w.writeEndElement();
        }

        BigDecimal vNF = vProd;
        w.writeStartElement("total"); w.writeStartElement("ICMSTot");
        for (String name : List.of("vBC", "vICMS", "vICMSDeson", "vFCP", "vBCST", "vST", "vFCPST", "vFCPSTRet")) {
            e(w, name, "0.00");
        }
        e(w, "vProd", money(vProd));
        for (String name : List.of("vFrete", "vSeg", "vDesc", "vII", "vIPI", "vIPIDevol", "vPIS", "vCOFINS", "vOutro")) {
            e(w, name, "0.00");
        }
        e(w, "vNF", money(vNF));
        w.writeEndElement(); w.writeEndElement();

        w.writeStartElement("transp"); e(w, "modFrete", "9"); w.writeEndElement();

        w.writeStartElement("pag");
        BigDecimal paid = BigDecimal.ZERO, change = BigDecimal.ZERO;
        for (JsonNode payment : fixture.path("payments")) {
            require("CASH".equals(str(payment, "method")), "Only CASH is mapped in the POC.");
            BigDecimal amount = dec(payment, "amount");
            paid = paid.add(amount);
            change = change.add(dec(payment, "change"));
            w.writeStartElement("detPag");
            e(w, "indPag", "0"); e(w, "tPag", "01"); e(w, "vPag", money(amount));
            w.writeEndElement();
        }
        require(paid.subtract(change).compareTo(vNF) == 0, "Payments minus change must equal vNF.");
        if (change.signum() > 0) {
            e(w, "vTroco", money(change));
        }
        w.writeEndElement();

        JsonNode tech = issuer.path("technicalResponsible");
        if (tech.isObject()) {
            w.writeStartElement("infRespTec");
            e(w, "CNPJ", str(tech, "cnpj")); e(w, "xContato", str(tech, "contact"));
            e(w, "email", str(tech, "email")); e(w, "fone", str(tech, "phone"));
            w.writeEndElement();
        }
        w.writeEndElement(); // infNFe

        // QR Code v3 online format as observed in SEFAZ-SP-authorized documents.
        w.writeStartElement("infNFeSupl");
        e(w, "qrCode", str(issuer, "qrCodeUrl") + "?p=" + accessKey + "|3|" + tpAmb);
        e(w, "urlChave", str(issuer, "consultationUrl"));
        w.writeEndElement();

        w.writeEndElement(); // NFe
        w.writeEndDocument();
        w.close();
        return new Result(buffer.toByteArray(), accessKey);
    }

    // Modulo 11 over the 43-digit key body, weights 2..9 from the right;
    // remainders 0 and 1 yield 0.
    static int accessKeyCheckDigit(String keyBody) {
        int sum = 0, weight = 2;
        for (int i = keyBody.length() - 1; i >= 0; i--) {
            sum += (keyBody.charAt(i) - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        int remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static void writeOutr(XMLStreamWriter w, String tax, JsonNode config, String rateName, String valueName) throws Exception {
        require((tax + "Outr").equals(str(config, "group")), "Only " + tax + "Outr is implemented.");
        BigDecimal rate = new BigDecimal(str(config, "rate"));
        require(rate.signum() == 0, "Only zero-rate " + tax + " is implemented.");
        w.writeStartElement(tax); w.writeStartElement(tax + "Outr");
        e(w, "CST", str(config, "cst")); e(w, "vBC", "0.00"); e(w, rateName, fixed(rate, 4)); e(w, valueName, "0.00");
        w.writeEndElement(); w.writeEndElement();
    }

    private static void e(XMLStreamWriter w, String name, String value) throws Exception {
        w.writeStartElement(name);
        w.writeCharacters(value);
        w.writeEndElement();
    }

    private static String money(BigDecimal value) { return fixed(value, 2); }

    // Same as .NET "0.0000"-style formats: midpoint away from zero.
    private static String fixed(BigDecimal value, int scale) {
        return value.setScale(scale, RoundingMode.HALF_UP).toPlainString();
    }

    // Same as .NET "0.00########": at least 2 and at most 10 decimals.
    private static String flexible(BigDecimal value) {
        BigDecimal rounded = value.setScale(10, RoundingMode.HALF_UP).stripTrailingZeros();
        return rounded.scale() < 2 ? rounded.setScale(2).toPlainString() : rounded.toPlainString();
    }

    private static String str(JsonNode node, String name) throws RefusedException {
        JsonNode value = node.get(name);
        if (value == null || !value.isTextual() || value.asText().isBlank()) {
            throw new RefusedException("Missing or empty string property '" + name + "'.");
        }
        return value.asText();
    }

    private static BigDecimal dec(JsonNode node, String name) throws RefusedException {
        String text = str(node, name);
        if (!text.matches("\\d+(\\.\\d+)?")) {
            throw new RefusedException("Property '" + name + "' is not a non-negative decimal string.");
        }
        return new BigDecimal(text);
    }

    private static void require(boolean condition, String message) throws RefusedException {
        if (!condition) {
            throw new RefusedException(message);
        }
    }

    static String utf8(byte[] bytes) { return new String(bytes, StandardCharsets.UTF_8); }
}
