# Implementation Plan: Fiscal Service Discovery and Foundation Design

**Branch**: `feature/001-fiscal-discovery` | **Date**: 2026-08-04 | **Spec**: [spec.md](./spec.md)

## Summary

Executar a Fase 0 do RRF5 Fiscal Service para reduzir riscos antes de código produtivo: congelar a fotografia normativa, comparar .NET 10, Java 25 e TypeScript, avaliar bibliotecas fiscais, provar A1/XMLDSig/XSD/SOAP/homologação SP, validar DANFE/QR Code e fechar as decisões de stack, arquitetura e operação. O resultado é uma base implementável para a futura branch `feature/002-fiscal-foundation`.

## Technical Context

**Candidatos de linguagem/runtime**: .NET 10 LTS (preferencial), Java 25 LTS (comparação), Node.js/TypeScript 20+ (baseline)

**Arquitetura alvo**: monólito modular independente; API e workers implantáveis separadamente

**Persistência alvo**: PostgreSQL para metadados; object storage S3-compatível para XML/PDF; Redis apenas auxiliar; fila durável RabbitMQ ou gerenciada

**Segurança alvo**: OAuth 2.0 Client Credentials, JWT curto, KMS/Vault por interface, envelope encryption para A1

**Integração**: REST/OpenAPI para clientes; SOAP/TLS/certificado A1 nos adaptadores SEFAZ; webhooks HMAC

**Observabilidade**: OpenTelemetry, logs estruturados, métricas de emissão/autorizador/fila/certificado

**Testes da descoberta**: golden XML, XSD, XMLDSig, mock SOAP, homologação SP controlada, concorrência/idempotência e licença/SBOM

**Escopo inicial**: NFC-e 65, São Paulo, leiaute 4.00 com versões vigentes, A1, homologação; sem produção real nesta branch

## Governance Check

- Serviço independente confirmado; nenhuma dependência interna do BurgoOS entra no núcleo.
- Regras fiscais concretas exigem fonte oficial e validação do responsável fiscal.
- Idempotência, tenant, ambiente e artefatos imutáveis são gates, não melhorias posteriores.
- Nenhum certificado/CSC real entra no Git, fixture, log ou relatório.
- A descoberta termina com decisão baseada em evidência; não implementa emissão produtiva.

## Project Structure Proposed

```text
src/
|-- Rrf5.Fiscal.Api/
|-- Rrf5.Fiscal.Worker/
|-- Rrf5.Fiscal.Domain/
|-- Rrf5.Fiscal.Application/
|-- Rrf5.Fiscal.Infrastructure/
|-- Rrf5.Fiscal.Adapters.Sefaz/
|-- Rrf5.Fiscal.Contracts/
tests/
|-- Unit/
|-- Contract/
|-- Integration/
|-- FiscalCases/
pocs/
|-- dotnet/
|-- java/
|-- typescript/
docs/
specs/001-fiscal-service/
```

Essa estrutura é uma hipótese para a POC .NET. A decisão final pode ajustar nomes, mas preserva fronteiras de domínio e adaptadores.

## Phase 0 — Research and POCs

1. Catalogar MOC, XSD, NTs, Web Services SP, QR Code e contingência com versão/hash/vigência.
2. Criar matriz objetiva de stack e biblioteca: cobertura, licença, manutenção, RTC, testabilidade, suporte e substituição.
3. Implementar POCs isoladas, sem banco de produção e sem abstrações prematuras.
4. Gerar o mesmo documento mínimo sanitizado, validar XSD e assinatura em cada candidato elegível.
5. Provar chamada SOAP mTLS/homologação com credencial de teste fornecida fora do repositório.
6. Comparar diagnóstico, portabilidade Linux/container, tempo de implementação, dependências e observabilidade.
7. Registrar decisão final nos ADR-011/012 e atualizar o roadmap.

## Phase 1 Design — Foundation Handoff

Entregar modelo de dados detalhado, máquina de estados, contratos OpenAPI versionados, quickstart de sandbox, threat model, backlog da fundação e critérios de entrada para implementação.

## POC Acceptance Gates

| Gate              | Evidência necessária                                                              |
| ----------------- | --------------------------------------------------------------------------------- |
| XML               | XML NFC-e mínimo determinístico e válido no XSD oficial catalogado                |
| Assinatura        | referência assinada e verificada; alteração posterior invalida a assinatura       |
| A1                | carregamento por secret externo, fingerprint/titularidade/validade, sem vazamento |
| SOAP              | envelope e TLS observáveis contra mock; homologação quando credenciais existirem  |
| Resultado incerto | simulação consulta antes de retransmissão                                         |
| DANFE/QR          | geração/validação conforme manual vigente ou lacuna explicitamente bloqueante     |
| Portabilidade     | build/test/container Linux reproduzíveis                                          |
| Licença           | licença e dependências compatíveis com uso comercial, registradas em SBOM         |
| Manutenção        | atividade e resposta às NTs recentes verificadas                                  |

## Decision Rules

- Escolher .NET 10 se cumprir todos os gates com menor risco total e biblioteca substituível.
- Escolher Java 25 se demonstrar cobertura/manutenção fiscal materialmente superior que compense o custo de stack.
- Escolher TypeScript somente se cumprir os mesmos gates sem implementar manualmente uma superfície fiscal desproporcional.
- Se nenhuma opção cumprir DANFE/RTC/contingência, não iniciar fundação; decidir fornecedor fiscal externo ou escopo reduzido.

## Risks

- Homologação indisponível ou credenciais ausentes: concluir mock/artefatos e marcar gate externo pendente, sem simular sucesso real.
- Biblioteca gera XML aceito, mas está atrasada em RTC: reprovar adoção ou encapsular apenas se houver plano verificável.
- POC vira código produtivo: proibir referência direta dos projetos `pocs/` pela solução final.
- Regras do contador ausentes: usar apenas fixture técnica sanitizada; não afirmar tributação correta.

## Post-Design Check

O plano preserva isolamento, atualização regulatória, testabilidade e MVP NFC-e/SP. A complexidade adicionada (fila, storage e cofre) é justificada por idempotência, guarda fiscal e certificado; Kubernetes e múltiplos microsserviços continuam fora.
