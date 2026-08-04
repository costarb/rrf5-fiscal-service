# ADR-011 — Biblioteca fiscal própria ou de terceiros

**Status:** Pendente de POC

## Decisão

Preferir biblioteca consolidada somente após validar manutenção, cobertura, RTC, licença comercial, testes, suporte e auditabilidade. Isolá-la atrás de interfaces próprias e fixtures oficiais. Implementar diretamente apenas lacunas/partes bem delimitadas.

## Consequências

Acelera entrega e reduz risco de protocolo, mas cria dependência regulatória. Deve existir plano de substituição e nenhum tipo da biblioteca aparece na API pública.
