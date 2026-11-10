package com.rrf5.fiscal.poc;

import java.io.IOException;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import javax.xml.XMLConstants;
import javax.xml.transform.Source;
import javax.xml.transform.stream.StreamSource;
import javax.xml.validation.Schema;
import javax.xml.validation.SchemaFactory;
import javax.xml.validation.Validator;
import org.xml.sax.SAXException;
import org.xml.sax.SAXParseException;

public final class Main {
    private static final String EXPECTED_PACKAGE_SHA256 =
            "b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf";

    private Main() {}

    public static void main(String[] args) throws Exception {
        // Diagnostics in a fixed locale and UTF-8 regardless of host settings, so the
        // JSON output is stable and readable (pt-BR console code pages garbled it).
        Locale.setDefault(Locale.ROOT);
        System.setOut(new PrintStream(System.out, true, StandardCharsets.UTF_8));
        System.setErr(new PrintStream(System.err, true, StandardCharsets.UTF_8));

        if (args.length == 0) {
            System.out.printf("{\"candidate\":\"java\",\"runtime\":\"%s\",\"scope\":\"fiscal-discovery-poc\",\"productionReady\":false,"
                    + "\"commands\":[\"validate --xml --xsd --package\",\"sign --pfx [--xml --out]\",\"verify --xml --cert-sha256\"]}%n",
                    Runtime.version());
            return;
        }

        Map<String, String> options = parseOptions(args);
        switch (args[0]) {
            case "validate" -> validate(options);
            case "sign" -> {
                System.err.println("{\"dsigPolicy\":\"" + XmlDsigCommands.relaxPolicyForNfe() + "\"}");
                XmlDsigCommands.sign(options);
            }
            case "verify" -> {
                System.err.println("{\"dsigPolicy\":\"" + XmlDsigCommands.relaxPolicyForNfe() + "\"}");
                XmlDsigCommands.verifyCommand(options);
            }
            case "generate" -> generate(options);
            case "bench" -> {
                System.err.println("{\"dsigPolicy\":\"" + XmlDsigCommands.relaxPolicyForNfe() + "\"}");
                Bench.run(options);
            }
            case "check-key" -> {
                String key = options.get("--key");
                if (key == null || !key.matches("\\d{44}")) {
                    fail("INVALID_KEY", "--key must be 44 digits.");
                }
                boolean valid = NfceGenerator.accessKeyCheckDigit(key.substring(0, 43)) == key.charAt(43) - '0';
                System.out.printf("{\"candidate\":\"java\",\"command\":\"check-key\",\"checkDigitValid\":%b}%n", valid);
            }
            default -> fail("UNKNOWN_COMMAND", "Supported commands: validate, sign, verify, generate, check-key.");
        }
    }

    // T016: same contract as the .NET POC. A synthetic issuer (all-zero CNPJ) may
    // write inside the repository and prints its access key; any other issuer is
    // taxpayer data: output must go outside the repository and the key is not printed.
    private static void generate(Map<String, String> options) throws Exception {
        var mapper = new com.fasterxml.jackson.databind.ObjectMapper();
        var fixture = mapper.readTree(Files.readAllBytes(requiredFile(options, "--case")));
        var profile = mapper.readTree(Files.readAllBytes(requiredFile(options, "--profile")));
        var issuer = mapper.readTree(Files.readAllBytes(requiredFile(options, "--issuer")));
        boolean synthetic = "00000000000000".equals(issuer.path("cnpj").asText());

        int nNF;
        java.time.OffsetDateTime dhEmi;
        String cNF = options.get("--cnf");
        try {
            nNF = Integer.parseInt(options.getOrDefault("--nnf", ""));
            dhEmi = java.time.OffsetDateTime.parse(options.getOrDefault("--dhemi", ""),
                    java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ssxxx"));
            if (cNF == null) {
                throw new IllegalArgumentException();
            }
        } catch (RuntimeException exception) {
            fail("INVALID_ARGUMENTS", "--nnf <int>, --cnf <8 digits> and --dhemi <yyyy-MM-ddTHH:mm:ss+hh:mm> are required; nothing is taken from the clock or RNG.");
            return;
        }

        NfceGenerator.Result result;
        try {
            result = NfceGenerator.generate(fixture, profile, issuer, nNF, cNF, dhEmi);
        } catch (NfceGenerator.RefusedException exception) {
            fail("GENERATION_REFUSED", exception.getMessage());
            return;
        }

        if (options.containsKey("--out")) {
            Path out = Path.of(options.get("--out")).toAbsolutePath().normalize();
            if (!synthetic && insideRepository(out)) {
                fail("OUTPUT_INSIDE_REPOSITORY", "A document for a real issuer carries taxpayer data; write it outside the repository.");
            }
            Files.write(out, result.xml());
        } else if (!synthetic) {
            fail("OUT_REQUIRED", "A real issuer requires --out outside the repository; the XML is not printed.");
        }

        System.out.printf("{\"candidate\":\"java\",\"command\":\"generate\",\"profile\":\"%s\",\"syntheticIssuer\":%b,"
                        + "\"accessKey\":%s,\"xmlSha256\":\"%s\",\"bytes\":%d}%n",
                jsonEscape(profile.path("profileId").asText()), synthetic,
                synthetic ? "\"" + result.accessKey() + "\"" : "null",
                XmlDsigCommands.sha256(result.xml()), result.xml().length);
    }

    private static void validate(Map<String, String> options) {
        try {
            Path xml = requiredFile(options, "--xml");
            Path xsd = requiredFile(options, "--xsd");
            Path packageZip = requiredFile(options, "--package");
            String packageHash = XmlDsigCommands.sha256(Files.readAllBytes(packageZip));
            if (!EXPECTED_PACKAGE_SHA256.equals(packageHash)) {
                fail("XSD_PACKAGE_HASH_MISMATCH", "Expected " + EXPECTED_PACKAGE_SHA256 + ", got " + packageHash + ".");
            }

            SchemaFactory factory = SchemaFactory.newInstance(XMLConstants.W3C_XML_SCHEMA_NS_URI);
            factory.setProperty(XMLConstants.ACCESS_EXTERNAL_DTD, "");
            factory.setProperty(XMLConstants.ACCESS_EXTERNAL_SCHEMA, "file");
            Source schemaSource = new StreamSource(xsd.toFile());
            Schema schema = factory.newSchema(schemaSource);
            Validator validator = schema.newValidator();
            validator.setProperty(XMLConstants.ACCESS_EXTERNAL_DTD, "");
            validator.setProperty(XMLConstants.ACCESS_EXTERNAL_SCHEMA, "file");
            validator.validate(new StreamSource(xml.toFile()));

            System.out.printf(
                    "{\"candidate\":\"java\",\"valid\":true,\"schema\":\"%s\",\"packageSha256\":\"%s\",\"messages\":[]}%n",
                    jsonEscape(xsd.getFileName().toString()), packageHash);
        } catch (SAXParseException exception) {
            System.out.printf(
                    "{\"candidate\":\"java\",\"valid\":false,\"line\":%d,\"column\":%d,\"message\":\"%s\"}%n",
                    exception.getLineNumber(), exception.getColumnNumber(), jsonEscape(exception.getMessage()));
            System.exit(2);
        } catch (IOException | SAXException exception) {
            fail("VALIDATION_ERROR", exception.getMessage());
        } catch (Exception exception) {
            fail("VALIDATION_ERROR", exception.toString());
        }
    }

    private static Map<String, String> parseOptions(String[] args) {
        if ((args.length - 1) % 2 != 0) {
            fail("INVALID_ARGUMENTS", "Options must be provided as --name <value> pairs.");
        }
        Map<String, String> options = new HashMap<>();
        for (int index = 1; index < args.length; index += 2) {
            if (!args[index].startsWith("--") || options.putIfAbsent(args[index], args[index + 1]) != null) {
                fail("INVALID_ARGUMENTS", "Invalid or duplicate option: " + args[index] + ".");
            }
        }
        return options;
    }

    static Path requiredFile(Map<String, String> options, String name) {
        String value = options.get(name);
        if (value == null || !Files.isRegularFile(Path.of(value))) {
            fail("FILE_NOT_FOUND", name + " must reference an existing file.");
        }
        return Path.of(value).toAbsolutePath().normalize();
    }

    // Nearest ancestor of the running jar/classes that contains .git; none in a
    // container image, so nothing is treated as inside a repository there.
    static boolean insideRepository(Path path) {
        try {
            Path start = Path.of(Main.class.getProtectionDomain().getCodeSource().getLocation().toURI()).toAbsolutePath();
            for (Path current = start; current != null; current = current.getParent()) {
                if (Files.isDirectory(current.resolve(".git"))) {
                    return path.toAbsolutePath().normalize().toString().toLowerCase(Locale.ROOT)
                            .startsWith(current.toString().toLowerCase(Locale.ROOT) + java.io.File.separator);
                }
            }
            return false;
        } catch (Exception exception) {
            return true; // fail closed
        }
    }

    static String jsonEscape(String value) {
        return value == null ? "" : value.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\r", "\\r").replace("\n", "\\n");
    }

    static void fail(String code, String message) {
        System.err.printf("{\"candidate\":\"java\",\"valid\":false,\"error\":\"%s\",\"message\":\"%s\"}%n",
                jsonEscape(code), jsonEscape(message));
        System.exit(1);
        throw new IllegalStateException("unreachable");
    }
}
