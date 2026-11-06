package com.rrf5.fiscal.poc;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.HashMap;
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
            "d44ae5aa6a0d1cabf6235d2d2d47b75be5dd87bc6b90a7ec3dcec99c3d41bda1";

    private Main() {}

    public static void main(String[] args) {
        if (args.length == 0) {
            System.out.println("{\"candidate\":\"java\",\"scope\":\"fiscal-discovery-poc\",\"productionReady\":false}");
            return;
        }

        if (!"validate".equals(args[0])) {
            fail("UNKNOWN_COMMAND", "Only the validate command is implemented in this T016 increment.");
        }

        try {
            Map<String, Path> options = parseOptions(args);
            Path xml = requiredFile(options, "--xml");
            Path xsd = requiredFile(options, "--xsd");
            Path packageZip = requiredFile(options, "--package");
            String packageHash = sha256(packageZip);
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
        }
    }

    private static Map<String, Path> parseOptions(String[] args) {
        if ((args.length - 1) % 2 != 0) {
            fail("INVALID_ARGUMENTS", "Options must be provided as --name <value> pairs.");
        }
        Map<String, Path> options = new HashMap<>();
        for (int index = 1; index < args.length; index += 2) {
            if (!args[index].startsWith("--") || options.putIfAbsent(args[index], Path.of(args[index + 1])) != null) {
                fail("INVALID_ARGUMENTS", "Invalid or duplicate option: " + args[index] + ".");
            }
        }
        return options;
    }

    private static Path requiredFile(Map<String, Path> options, String name) {
        Path path = options.get(name);
        if (path == null || !Files.isRegularFile(path)) {
            fail("FILE_NOT_FOUND", name + " must reference an existing file.");
        }
        return path.toAbsolutePath().normalize();
    }

    private static String sha256(Path path) throws IOException {
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path));
            return java.util.HexFormat.of().formatHex(digest);
        } catch (java.security.NoSuchAlgorithmException exception) {
            throw new IllegalStateException("SHA-256 is unavailable.", exception);
        }
    }

    private static String jsonEscape(String value) {
        return value == null ? "" : value.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\r", "\\r").replace("\n", "\\n");
    }

    private static void fail(String code, String message) {
        System.err.printf("{\"candidate\":\"java\",\"valid\":false,\"error\":\"%s\",\"message\":\"%s\"}%n",
                jsonEscape(code), jsonEscape(message));
        System.exit(1);
        throw new IllegalStateException("unreachable");
    }
}
