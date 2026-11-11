# ADR-009 — Versionamento de schemas

**Status:** Aceito com evidência da fundação (2026-11-11), sujeito ao sign-off da 002  

## Decisão

Schemas/XSD/manuais/NTs são artefatos imutáveis com versão, hash e vigência. Seleção depende de modelo, UF, ambiente e data; release de regra é independente da versão da API.

## Consequências

Reproduz documentos antigos e permite coexistência RTC. Requer pipeline de ingestão/verificação e impede substituir silenciosamente arquivo de mesma versão.

## Implementação na fundação (2026-11-11)

Catálogo embutido com hash verificado, carregado em `schema_version`; seleção por autorizador, ambiente e data; elementos exclusivos de pacote desligados até liberação auditada com evidência; artefatos oficiais guardados por hash com object lock e verificação na leitura. Evidência: `SchemaSelectionTests`, `RegulatoryStorageTests`, `SchemaSelectionApiTests`.
