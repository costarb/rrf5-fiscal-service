# ADR-009 — Versionamento de schemas

**Status:** Proposto

## Decisão

Schemas/XSD/manuais/NTs são artefatos imutáveis com versão, hash e vigência. Seleção depende de modelo, UF, ambiente e data; release de regra é independente da versão da API.

## Consequências

Reproduz documentos antigos e permite coexistência RTC. Requer pipeline de ingestão/verificação e impede substituir silenciosamente arquivo de mesma versão.
