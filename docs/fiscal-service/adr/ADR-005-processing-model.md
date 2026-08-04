# ADR-005 — Processamento síncrono e assíncrono

**Status:** Proposto

## Decisão

Uma única máquina persistente atende ambos os modos. Síncrono espera até limite configurado e degrada para `202`; assíncrono retorna imediatamente. Workers consomem fila durável com entrega pelo menos uma vez.

## Consequências

Sem lógica fiscal duplicada. Clientes devem consultar status; jobs e handlers são idempotentes. Resultado externo pode exceder timeout sem virar falha definitiva.
