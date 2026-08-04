# ADR-006 — Idempotência

**Status:** Aceito para especificação

## Decisão

Toda mutação usa `Idempotency-Key`; escopo inclui tenant, estabelecimento e operação. Persistir hash canônico, recurso e resposta. Mesmo payload reproduz; payload diferente conflita. Número e envio são associados ao claim persistido.

## Consequências

Previne duplicidade do cliente, mas não basta após timeout: antes de retransmitir, consultar recibo/protocolo/chave. Registros não expiram enquanto houver risco fiscal associado.
