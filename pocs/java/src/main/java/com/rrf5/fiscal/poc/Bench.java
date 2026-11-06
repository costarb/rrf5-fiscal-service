package com.rrf5.fiscal.poc;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.lang.management.ManagementFactory;
import java.nio.file.Files;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.cert.X509Certificate;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.Arrays;
import java.util.Collections;
import java.util.Map;

// Performance comparison: generate -> sign -> verify per iteration with a fresh nNF,
// after a warm-up. Synthetic inputs and a throwaway key only.
final class Bench {
    private Bench() {}

    static void run(Map<String, String> options) throws Exception {
        ObjectMapper mapper = new ObjectMapper();
        JsonNode fixture = mapper.readTree(Files.readAllBytes(Main.requiredFile(options, "--case")));
        JsonNode profile = mapper.readTree(Files.readAllBytes(Main.requiredFile(options, "--profile")));
        JsonNode issuer = mapper.readTree(Files.readAllBytes(Main.requiredFile(options, "--issuer")));
        int iterations = Integer.parseInt(options.getOrDefault("--iterations", "2000"));
        int warmup = Integer.parseInt(options.getOrDefault("--warmup", "200"));

        String password = System.getenv("RRF5_A1_PASSWORD");
        if (password == null || password.isEmpty()) {
            Main.fail("A1_PASSWORD_MISSING", "Set RRF5_A1_PASSWORD.");
        }
        KeyStore store = KeyStore.getInstance("PKCS12");
        try (var input = Files.newInputStream(Main.requiredFile(options, "--pfx"))) {
            store.load(input, password.toCharArray());
        }
        String alias = Collections.list(store.aliases()).stream().filter(a -> {
            try { return store.isKeyEntry(a); } catch (Exception e) { return false; }
        }).findFirst().orElseThrow();
        PrivateKey key = (PrivateKey) store.getKey(alias, password.toCharArray());
        X509Certificate certificate = (X509Certificate) store.getCertificate(alias);
        OffsetDateTime dhEmi = OffsetDateTime.of(2026, 11, 6, 12, 0, 0, 0, ZoneOffset.ofHours(-3));

        for (int i = 1; i <= warmup; i++) {
            if (!once(fixture, profile, issuer, i, dhEmi, key, certificate)) {
                Main.fail("BENCH_VERIFY_FAILED", "Warm-up iteration failed verification.");
            }
        }

        var threads = (com.sun.management.ThreadMXBean) ManagementFactory.getThreadMXBean();
        long threadId = Thread.currentThread().threadId();
        long allocatedBefore = threads.getThreadAllocatedBytes(threadId);
        double[] samples = new double[iterations];
        long totalStart = System.nanoTime();
        for (int i = 0; i < iterations; i++) {
            long started = System.nanoTime();
            if (!once(fixture, profile, issuer, warmup + 1 + i, dhEmi, key, certificate)) {
                Main.fail("BENCH_VERIFY_FAILED", "Iteration failed verification.");
            }
            samples[i] = (System.nanoTime() - started) / 1_000_000.0;
        }
        double totalSeconds = (System.nanoTime() - totalStart) / 1_000_000_000.0;
        long allocated = threads.getThreadAllocatedBytes(threadId) - allocatedBefore;
        Arrays.sort(samples);

        System.out.printf(java.util.Locale.ROOT,
                "{\"candidate\":\"java\",\"command\":\"bench\",\"runtime\":\"%s\",\"iterations\":%d,\"warmup\":%d,"
                        + "\"opsPerSecond\":%.1f,\"p50Ms\":%.3f,\"p95Ms\":%.3f,\"p99Ms\":%.3f,\"allocatedKbPerOp\":%.1f}%n",
                Runtime.version(), iterations, warmup, iterations / totalSeconds,
                percentile(samples, 0.50), percentile(samples, 0.95), percentile(samples, 0.99),
                allocated / 1024.0 / iterations);
    }

    private static boolean once(JsonNode fixture, JsonNode profile, JsonNode issuer, int nNF, OffsetDateTime dhEmi,
                                PrivateKey key, X509Certificate certificate) throws Exception {
        NfceGenerator.Result result = NfceGenerator.generate(fixture, profile, issuer, nNF, "12345678", dhEmi);
        String signed = XmlDsigCommands.signDocument(XmlDsigCommands.parse(result.xml()), key, certificate);
        return XmlDsigCommands.verify(signed, "NFe" + result.accessKey(), certificate).verified();
    }

    private static double percentile(double[] sorted, double p) {
        return sorted[Math.min(sorted.length - 1, (int) Math.ceil(p * sorted.length) - 1)];
    }
}
