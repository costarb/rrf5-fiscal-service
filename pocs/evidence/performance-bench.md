# Performance comparison — generate → sign → verify

**Status:** DONE (development host; relative comparison only)  
**Executed:** 2026-11-06

## Method

Same work in every candidate, per iteration: generate the golden NFC-e with a new
`nNF` (so the access key and digest change), sign it (C14N, RSA-SHA1, RSA 2048
throwaway key), verify it with the fail-closed verifier. 200 warm-up iterations,
then 2000 measured, 3 runs (Java tuned: 2). Synthetic inputs only.

- Commands: `bench` in `pocs/dotnet` (Release build), `pocs/java`, `pocs/typescript`.
- Peak working set sampled externally every 15 ms by a PowerShell harness (outside Git);
  startup = median of 5 no-argument invocations.
- Host: Intel Core i7-11390H (4 cores/8 threads), 15.7 GB RAM, Windows 11, laptop.
  Run-to-run variance is high (turbo/thermal/background load); treat numbers as ratios,
  not production capacity. Production would run on Linux containers.

Runtimes: .NET 10.0.14; Temurin 25.0.5; Node.js 20.20.2 with `tsx` (Node 20 is EOL;
a production service would use Node 26 LTS).

## Results

| Candidate | ops/s (runs) | p50 ms | p95 ms | p99 ms | Alloc/op | Peak working set | Startup |
| --- | --- | --- | --- | --- | --- | --- | --- |
| .NET 10 | 72.7 / 72.8 / 78.8 | 9.4–10.1 | 26.6–36.0 | 38–60 | 916 KB | **61 MB** | 1.0 s |
| Java 25, JVM defaults (G1, heap up to 1/4 RAM) | 50.2 / 43.8 / 34.6 | 17.5–20.5 | 37–72 | 53–111 | 392 KB | 256 MB | 1.0 s |
| Java 25, `-Xmx128m -XX:+UseSerialGC` | 73.1 / 77.3 | 10.9–11.5 | 26.0–26.7 | 45–49 | 391 KB | 138 MB | 1.3 s |
| TypeScript (Node 20) | 12.0 / 11.1 / 12.9 | 69–81 | 137–161 | 195–218 | n/a | 141–146 MB | 4.2 s* |

\* includes on-the-fly TypeScript transpilation by `tsx`; a compiled build would start faster.

### Where TypeScript spends its time (`src/bench-split.ts`, 300 iterations)

| Step | ms/op |
| --- | --- |
| generate | 0.9 |
| sign (`xml-crypto`) | 34.3 |
| verify (structural checks + `xml-crypto`) | 65.3 |
| raw Node RSA-2048 SHA-1 signature, for reference | 2.5 |

The cost is in the XML layer of `xml-crypto` (XPath over xmldom, JavaScript
canonicalization), not in cryptography.

## Reading

- **.NET 10** has the best throughput together with the lowest memory (about 61 MB)
  and needs no tuning.
- **Java 25** matches .NET throughput once the heap and GC are sized for a small
  service, using about twice the memory; with JVM defaults it is slower and uses ~4× the memory.
  Production would need explicit JVM sizing.
- **TypeScript** is about 6× slower per document and uses more than twice the memory of .NET.
  The bottleneck is the third-party XMLDSig library.

## What this means for the service

An NFC-e authorization waits 0.5–2 s on SEFAZ (`homologation.md`: 1655 ms round trip),
so even the TypeScript per-document CPU cost (~100 ms) is not the user-visible limit at
low volume. It matters for cost and headroom: CPU per document on shared/small
instances, memory per container (hosting price tiers are memory-based), and
batch work (re-signing, contingency transmission, reprocessing). On those, .NET is the
most efficient candidate, with Java close behind when tuned.
