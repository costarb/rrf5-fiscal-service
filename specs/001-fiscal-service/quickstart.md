# Quickstart: Discovery POCs

## Preconditions

- no production certificate, CSC or taxpayer data;
- test A1 and credentials supplied via local secret store/environment outside Git;
- official XSD/manual catalog entry contains URL, version and SHA-256;
- Docker-compatible runtime available;
- fiscal fixture approved as technical-only or by responsible accountant.

## Run order

1. Execute repository secret scan and verify clean worktree.
2. Generate a minimal normalized NFC-e fixture.
3. Run XSD-only validation and record exact validation messages.
4. Load A1, validate fingerprint/tax ID/expiry, sign the correct XML reference and verify it.
5. Mutate one signed field and confirm signature validation fails.
6. Produce SOAP envelope against a local mock; capture sanitized trace and timeout behavior.
7. Simulate authorization followed by connection loss; confirm protocol/access-key consultation precedes retry.
8. Generate or validate DANFE/QR result against the current manual.
9. Build and test Linux container without embedding secrets or normative test credentials.
10. Fill the scorecard and ADR decision with evidence links.

## Candidate scorecard

| Criterion                     | Weight |
| ----------------------------- | -----: |
| Official layout/RTC coverage  |     20 |
| XMLDSig/XSD correctness       |     15 |
| SEFAZ/SOAP/homologation       |     15 |
| DANFE/QR/contingency coverage |     10 |
| Maintenance/NT response       |     10 |
| License/commercial support    |     10 |
| Diagnostics/testability       |      8 |
| Linux/container/security      |      7 |
| Team delivery cost            |      5 |

Any failure in certificate secrecy, signature correctness, tenant/environment isolation or idempotent uncertain-result handling is eliminatory regardless of score.

## Required evidence

- command and sanitized output;
- fixture ID and artifact hashes;
- runtime/library/version/SBOM;
- official reference version;
- pass/fail, limitation and follow-up;
- no secret or complete personal/fiscal payload in logs.

## Exit

Update ADR-011 and ADR-012 to `Accepted`, document rejected alternatives, close external gates or mark them explicitly blocked, and create `feature/002-fiscal-foundation` from `develop` only after review.
