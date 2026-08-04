# ADR-001 — Estilo arquitetural

**Status:** Proposto — 04/08/2026

## Decisão

Criar repositório independente e monólito modular, com API e workers implantáveis separadamente. Banco e domínio são próprios. Não iniciar com múltiplos microsserviços.

## Consequências

Isola risco fiscal do BurgoOS e permite terceiros, preservando simplicidade transacional. Exige novo pipeline/deploy e contrato remoto. Extração futura ocorre por limites já definidos (certificados, documentos, webhooks), baseada em escala/organização.
