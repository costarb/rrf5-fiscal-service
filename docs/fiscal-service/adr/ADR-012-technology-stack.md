# ADR-012 — Stack tecnológica

**Status:** Aceito pelo operador em 2026-11-07  
**Data:** 2026-11-07 (recomendação inicial em 2026-11-06)

## Contexto

O serviço fiscal é independente do BurgoOS (FR-001, ADR-001); a fronteira é
OpenAPI/OAuth/webhook, então a stack pode diferir. A Fase 0 comparou .NET 10 LTS,
Java 25 LTS e TypeScript (Node LTS) com a mesma matriz e os gates de `plan.md`.
Prioridades do responsável: segurança e desempenho; linguagem secundária; equipe de
uma pessoa assistida por LLM, com mais entregas em .NET.

Critério do plano: escolher .NET 10 se cumprir todos os gates com menor risco total e
biblioteca substituível; escolher Java 25 só com cobertura/manutenção fiscal
materialmente superior.

## Evidência

Scorecard ponderado em `pocs/scorecards/runtime-library-scorecard.md` (T029):

| | .NET 10 | Java 25 | TypeScript |
| --- | --- | --- | --- |
| Média ponderada | **4,74** | 3,68 (4,11 se concluídos SOAP e container) | 2,58 |
| Segurança da cadeia de assinatura | biblioteca padrão, sem ajustes | biblioteca padrão com override da política de validação segura do JDK | `xml-crypto`/`node-forge` com histórico de falhas críticas |
| Desempenho (`performance-bench.md`) | ~73–79 ops/s, 61 MB | mesma vazão só com JVM ajustada, 138 MB | ~12 ops/s, ~143 MB |
| Homologação SEFAZ-SP | autorização + consulta, host e container Linux | assinatura idêntica a uma autorizada; sem SOAP | só TLS |

Gates fechados para .NET desde a recomendação:

- **T020 DANFE/QR:** PASS na emissão online, QR v3 validado por leitura no celular
  (`danfe-qr.md`).
- **T031 / ADR-011:** implementação própria sobre as bibliotecas da plataforma; nenhuma
  biblioteca fiscal de terceiros no runtime, logo nada a substituir.
- **T030 SBOM e licenças:** MIT (Microsoft, QRCoder); 0 vulnerabilidades na árvore da
  aplicação; SBOMs CycloneDX em `pocs/evidence/sbom/` (`dependency-review.md`).

## Decisão

**Adotar .NET 10 LTS** para o RRF5 Fiscal Service.

- Cadeia crítica (XML, XSD, XMLDSig, PKCS#12, TLS, SOAP) sobre a biblioteca padrão
  (`System.Security.Cryptography.Xml`, `XmlSchemaSet`, `HttpClient`), conforme ADR-011.
- Imagem de produção em Linux, não-root, sobre a base
  `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` (ou `aspnet` chiseled para o
  host HTTP): 2 achados médios contra 28 da base Ubuntu completa, metade do tamanho.
  Scan de imagem como gate de release (falha em alto/crítico com correção disponível).
- Infra comum mantida: PostgreSQL, fila durável (RabbitMQ ou gerenciada), object storage
  S3-compatível, Redis apenas auxiliar, KMS/Vault, OpenTelemetry. Kubernetes fora do MVP.
  A escolha concreta de fila e provedores fica para a fundação (`feature/002`).

Requisitos que acompanham a decisão:

- raízes ICP-Brasil fixadas por autorizador, sem desligar validação TLS
  (`container-poc.md`);
- versão de schema por autorizador/ambiente/data, sempre a partir do pacote oficial
  congelado com hash;
- arredondamento definido pelo leiaute, não pelo padrão da linguagem;
- `System.Drawing.Common` fixado em versão suportada enquanto o QRCoder o puxar; remover
  da árvore publicada quando possível;
- SBOM CycloneDX e scan emitidos pelo pipeline de release, não copiados da discovery.

## Consequências

- Suporte do .NET 10 até 2028-11-14: planejar a migração para a próxima LTS antes dessa
  data e tratá-la como entrega com regressão completa contra golden XML e homologação.
- Concentração em uma stack que o responsável domina reduz custo de manutenção; a
  fronteira OpenAPI mantém o BurgoOS desacoplado da escolha.
- Java 25 permanece alternativa documentada (assinatura comprovada); reabrir apenas se a
  manutenção fiscal própria em .NET se mostrar inviável.

## Pendências que não bloqueiam a stack

- T011: ratificação do perfil fiscal pelo contador (independente de stack).
- Impressão do DANFE em impressora térmica (`danfe-qr.md`).
- Eventos (cancelamento, inutilização) e contingência offline ainda não implementados;
  são o principal risco do caminho próprio (ADR-011).
