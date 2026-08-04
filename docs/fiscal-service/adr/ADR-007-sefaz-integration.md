# ADR-007 — Integração com SEFAZ

**Status:** Proposto

## Decisão

Adaptadores por modelo/autorizador/UF resolvidos por catálogo versionado de endpoints e capacidades. Domínio fala interfaces normalizadas; SOAP, envelopes, códigos e certificados ficam no adaptador.

## Consequências

Permite SP primeiro e expansão sem condicionais espalhadas. Catálogo errado é risco crítico; alterações exigem fonte oficial, homologação e dupla aprovação.
