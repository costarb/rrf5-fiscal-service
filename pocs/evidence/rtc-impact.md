# RTC impact checklist

Baseline: NT 2025.002 v1.40, checked through the official NF-e portal on **2026-08-04**. This checklist identifies engineering impact; it is not tax advice.

- [ ] Freeze the official NT PDF, annexes and applicable XSD package with SHA-256.
- [ ] Map IBS, CBS and related totals into a versioned domain model without hard-coding tenant tax decisions.
- [ ] Preserve optionality and coexistence with legacy tax groups during transition periods.
- [ ] Represent effective dates in regulatory configuration and select rules by document issue time.
- [ ] Validate new rejection rules using golden positive and negative fiscal cases.
- [ ] Confirm whether protocol, access-key and tax-identifier fields require alphanumeric-safe handling.
- [ ] Ensure persistence and webhooks retain the schema and regulatory-rule version used for each attempt.
- [ ] Keep XML generation deterministic so schema/NT regressions can be compared byte-for-byte before signing.
- [ ] Assess DANFE changes independently from XML authorization.
- [ ] Obtain written fiscal review for applicability, chronology and tenant-specific configuration.

Any unchecked item blocks a claim of RTC readiness. The POC may demonstrate technical field support but cannot infer correct taxation from authorization.

