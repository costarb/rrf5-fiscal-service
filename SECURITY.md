# Security Policy

## Scope

This repository handles designs and proofs of concept for Brazilian fiscal documents. Certificate material, CSC credentials, taxpayer data and production XML are confidential even during discovery.

## Secret handling

- Never commit A1 certificates, private keys, CSC identifiers/tokens, passwords, access tokens or production XML/PDF.
- Load test credentials only from an external secret store or local environment ignored by Git.
- Use synthetic, sanitized fixtures. Names, tax identifiers, addresses and transaction identifiers must not identify a real person or company.
- Logs may contain correlation IDs, hashes and certificate fingerprints, but never secret values, full authorization headers or private customer data.
- CI and container images must not embed credentials in source, layers, build arguments or artifacts.
- Rotate a credential immediately if it is exposed and purge it from Git history through the incident process.

## Certificates

Only disposable homologation certificates supplied outside this repository may be used in POCs. Record issuer, subject, fingerprint and validity as sanitized metadata; do not copy the certificate itself into evidence.

## Reporting a vulnerability

Report suspected vulnerabilities privately to the repository owner. Include affected component, reproduction steps and impact. Do not open a public issue containing credentials, fiscal data or an exploitable proof before remediation is coordinated.

