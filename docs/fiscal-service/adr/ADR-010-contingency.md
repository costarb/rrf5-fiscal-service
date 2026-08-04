# ADR-010 — Estratégia de contingência

**Status:** Proposto

## Decisão

Implementar somente modalidades oficiais aplicáveis. NFC-e offline exige persistência durável, documento/número original, DANFE específico e transmissão/reconciliação posterior. Sem conectividade com o serviço, contingência real requer agente local futuro.

## Consequências

O MVP cloud não promete operação offline total do PDV sem agente. Fila simples não será rotulada contingência. Rejeição posterior abre exceção operacional crítica.
