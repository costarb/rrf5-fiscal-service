# ADR-003 — Armazenamento de XML

**Status:** Proposto

## Decisão

Metadados no PostgreSQL; XML processado, eventos e DANFE em object storage versionado e cifrado. Guardar URI opaca, hash, tamanho, content type, versão e política de retenção. Object lock/legal hold quando disponível.

## Consequências

Evita inflar banco e melhora durabilidade/download. Disponibilidade do storage entra no fluxo; upload e metadados usam padrão outbox/estado para não declarar autorização sem artefato recuperável.
