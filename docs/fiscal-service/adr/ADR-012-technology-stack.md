# ADR-012 — Stack tecnológica

**Status:** Pendente de POC

## Decisão preliminar

Avaliar .NET LTS e Java LTS com a mesma matriz: A1, XMLDSig, canonicalização, XSD, SOAP/TLS, DANFE/QR, biblioteca/licença, OpenTelemetry, containers e homologação. Preferência inicial: .NET, se a POC e a capacidade da equipe forem aprovadas. Node/TypeScript não é descartado, mas não será padrão por conveniência.

Infra comum: PostgreSQL, fila durável, S3 compatível, Redis auxiliar, KMS/Vault e OpenTelemetry. Kubernetes fica fora do MVP.

## Consequências

O novo projeto pode usar stack diferente do BurgoOS porque a fronteira é OpenAPI/OAuth/webhook. A escolha final só ocorre na Fase 0 com evidência e custo de manutenção.
