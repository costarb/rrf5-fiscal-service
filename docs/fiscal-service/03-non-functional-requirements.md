# Requisitos não funcionais

## Metas iniciais controláveis

| Capacidade       | Meta MVP                                                               |
| ---------------- | ---------------------------------------------------------------------- |
| API de aceite    | 99,9% mensal; p95 ≤ 500 ms sem tempo da SEFAZ                          |
| Emissão síncrona | timeout controlado; latência reportada separando serviço e autorizador |
| Assíncrono       | 99% dos jobs elegíveis iniciados em até 5 s                            |
| Consulta         | p95 ≤ 300 ms para metadados                                            |
| DANFE            | p95 ≤ 2 s quando não estiver pré-gerado                                |
| Webhook          | primeira tentativa em até 10 s após estado persistido                  |
| Throughput       | 50 solicitações/s por implantação inicial, escalável horizontalmente   |
| RPO              | ≤ 5 min para metadados; zero lógico para artefatos confirmados         |
| RTO              | ≤ 60 min no MVP; plano degradado documentado                           |

SLA externo da SEFAZ não compõe o SLA controlável. Dashboards devem apresentar os dois tempos.

## Consistência e resiliência

- consistência forte para número, série, idempotência e transição de estado;
- entrega pelo menos uma vez em jobs/webhooks, consumidores idempotentes;
- circuit breaker por autorizador/ambiente;
- retentativa apenas de falha transitória e nunca de rejeição fiscal;
- backups cifrados, restauração trimestralmente exercitada;
- object storage versionado, retenção e verificação de hash.

## Retenção

Políticas são configuradas por categoria e obrigação legal. XML, protocolo e eventos fiscais seguem guarda legal aplicável; logs técnicos têm retenção curta e minimização; auditoria mantém prazo definido por risco/compliance. Legal hold impede descarte. A política concreta deve ser aprovada por jurídico/contador.

## Manutenibilidade

- contratos públicos compatíveis e versionados;
- adaptadores fiscais isolados;
- regras sem condicionais espalhadas;
- schemas reproduzíveis por hash;
- rollback de software não reverte documento, protocolo ou numeração;
- feature flags de vigência, nunca ativação manual silenciosa de regra fiscal.
