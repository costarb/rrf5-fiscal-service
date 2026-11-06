# ADR-012 — Stack tecnológica

**Status:** Recomendada — .NET 10 LTS (06/11/2026); aceite pendente de T020, ADR-011 e T030

## Decisão preliminar

Avaliar .NET LTS e Java LTS com a mesma matriz: A1, XMLDSig, canonicalização, XSD, SOAP/TLS, DANFE/QR, biblioteca/licença, OpenTelemetry, containers e homologação. Preferência inicial: .NET, se a POC e a capacidade da equipe forem aprovadas. Node/TypeScript não é descartado, mas não será padrão por conveniência.

Infra comum: PostgreSQL, fila durável, S3 compatível, Redis auxiliar, KMS/Vault e OpenTelemetry. Kubernetes fica fora do MVP.

## Consequências

O novo projeto pode usar stack diferente do BurgoOS porque a fronteira é OpenAPI/OAuth/webhook. A escolha final só ocorre na Fase 0 com evidência e custo de manutenção.

## Recomendação da Fase 0 (06/11/2026)

Scorecard ponderado em `pocs/scorecards/runtime-library-scorecard.md`, com prioridades do responsável (segurança e desempenho; linguagem secundária) e serviço independente confirmado:

- **.NET 10 LTS: 4,74/5** — XMLDSig, PKCS#12 e TLS na biblioteca padrão, sem ajustes; melhor vazão com menor memória (~61 MB); único candidato com autorização e consulta reais na SEFAZ-SP, em host e em container Linux.
- **Java 25 LTS: 3,68/5** (4,11 se concluídas SOAP e container) — assinatura idêntica à autorizada; exige ajuste da política de validação segura do JDK (SHA-1) e dimensionamento explícito da JVM.
- **TypeScript (Node LTS): 2,58/5** — funcionalmente viável e com geração/assinatura idênticas, mas cerca de 6× mais lento e com a cadeia de assinatura/PKCS#12 em bibliotecas de terceiros com histórico de falhas críticas.

Requisitos que valem para qualquer stack: raízes ICP-Brasil fixadas por autorizador (sem desligar validação TLS), versão de schema por autorizador/ambiente/data e regra de arredondamento definida pelo leiaute, não pelo padrão da linguagem. Planejar migração para a próxima LTS do .NET antes de 14/11/2028.

