# Implementation Plan: Fiscal Service Foundation

**Branch**: `feature/002-fiscal-foundation` | **Date**: 2026-11-09 | **Spec**: [spec.md](./spec.md)

## Summary

Construir a base de produto do RRF5 Fiscal Service em .NET 10: identidade e
isolamento multi-tenant, API administrativa, cadastro fiscal do emitente,
numeração atômica, cofre de A1/CSC, perfis fiscais versionados com aprovação de
quatro olhos, catálogo normativo com seleção de schema, auditoria e
observabilidade. Tudo roda numa stack local reproduzível. A fundação termina com
dois tenants em onboarding completo e **sem emissão**; a Fase 2 acrescenta a
emissão em homologação sobre esta base.

## Decisões de entrada

| # | Decisão | Origem |
| --- | --- | --- |
| D1 | .NET 10 LTS; imagem de produção em base chiseled, não-root | ADR-012 |
| D2 | Implementação fiscal própria; nenhuma biblioteca fiscal de terceiros no runtime | ADR-011 |
| D3 | Execução local primeiro: PostgreSQL, MinIO, RabbitMQ e Vault em Docker Compose, atrás de interfaces. Provedor de nuvem escolhido antes da primeira emissão em homologação | Operador, 2026-11-09 |
| D4 | Administração só por API, com scopes administrativos; painel depois, sobre a mesma API | Operador, 2026-11-09 |
| D5 | Papel de aprovador fiscal com credencial própria; quem cria a versão não aprova | Operador, 2026-11-09 |
| D6 | Perfil fiscal (cadastro, versão, vigência e aprovação) dentro da fundação; uso na emissão fica para a Fase 2 | Operador, 2026-11-09 |
| D7 | Na SP, só conteúdo comum a 010e–010g até a sonda do 010g ser aceita | E4, `discovery-signoff.md` |

## Decisões a registrar nesta feature

| ADR | Tema | Recomendação inicial (a confirmar no ADR) |
| --- | --- | --- |
| ADR-013 | Emissor de token | Endpoint próprio `/oauth/token` só com `client_credentials`; segredo de aplicação com hash (PBKDF2/Argon2 da plataforma); JWT assinado com chave do Vault, validade de 5 a 15 min. Alternativa avaliada: IdP externo (Keycloak) |
| ADR-014 | Acesso a dados e migrações | Npgsql com SQL explícito e migrações SQL versionadas, porque o modelo depende de chaves compostas, RLS e triggers; ORM só se preservar isso sem SQL escondido |
| ADR-015 | Modelo de regra fiscal | Payload declarativo com JSON Schema por versão do gerador; estados `DRAFT → PENDING_APPROVAL → APPROVED → RETIRED`; vigência sem sobreposição por dimensões |

ADR-002 a ADR-010 seguem "Proposto". Cada um que a fundação implementar passa a
"Aceito" com a evidência correspondente.

## Technical Context

**Runtime**: .NET 10 LTS, ASP.NET Core (minimal APIs), worker em `BackgroundService`

**Persistência**: PostgreSQL 17 (metadados, RLS); MinIO como S3-compatível (artefatos imutáveis, versioning e object lock em modo compliance quando disponível)

**Fila**: RabbitMQ com entrega pelo menos uma vez; nesta fase só para jobs internos (alerta de certificado, carga de catálogo)

**Segredos**: HashiCorp Vault (transit para envelope encryption do A1 e CSC; chave de assinatura de token) atrás de `ICertificateVault` e `ISecretStore`

**Contrato**: OpenAPI 3.1 em `specs/002-fiscal-foundation/contracts/openapi.yaml`, derivado do contrato da 001

**Observabilidade**: OpenTelemetry (traces, métricas), logs estruturados com redação por allowlist

**Testes**: xUnit; Testcontainers ou a própria stack do Compose para integração; testes de restrição do banco do `data-model.md`; testes de canário; testes de contrato

**Dependências**: cada pacote novo passa pelo gate de `dependency-review.md` (licença, SBOM, vulnerabilidade) antes de entrar

## Project Structure

```text
src/
|-- Rrf5.Fiscal.Api/              # HTTP, auth, problem details, OpenAPI
|-- Rrf5.Fiscal.Worker/           # jobs internos
|-- Rrf5.Fiscal.Domain/           # entidades, regras de consistência, sem I/O
|-- Rrf5.Fiscal.Application/      # casos de uso, portas
|-- Rrf5.Fiscal.Infrastructure/   # Postgres, MinIO, RabbitMQ, Vault, telemetria
|-- Rrf5.Fiscal.Regulatory/       # catálogo, SchemaVersion, resolução por autorizador/data
db/
|-- migrations/                   # SQL versionado
tests/
|-- Unit/
|-- Integration/                  # stack local
|-- Database/                     # restrições, RLS, concorrência
|-- Contract/
|-- Security/                     # canários, isolamento entre tenants
deploy/
|-- compose/                      # docker-compose.yml da stack local
|-- docker/                       # Dockerfile chiseled
```

`Rrf5.Fiscal.Adapters.Sefaz` e o gerador de XML entram na Fase 2. Nenhum projeto
de `src/` referencia `pocs/`; código da POC é reescrito com testes, não copiado.

## Governance Check

- Nenhum certificado, CSC ou segredo real no Git, em fixture, log ou relatório; testes usam material descartável "NOT AN A1".
- Nenhuma regra tributária é inventada: o perfil da POC entra como rascunho e só é aprovado após a ratificação do contador (T011).
- O serviço valida consistência e suporte do gerador; não escolhe CST, CFOP nem alíquota.
- Isolamento por tenant e ambiente é garantido no banco, além da aplicação.
- Fila técnica não é contingência fiscal; não há contingência nesta fase.

## Fases

1. **Setup**: solução, build e testes no CI, stack local, imagem chiseled com SBOM e scan.
2. **Base comum**: migrações do `data-model.md`, RLS e contexto de tenant, idempotência, auditoria, problem details, telemetria com redação.
3. **F1 — Identidade**: tenants, aplicações, emissor de token, scopes e concessão de estabelecimentos.
4. **F2 — Cadastro do emitente**: empresa, estabelecimento, ambiente, configuração e séries; alocação atômica de número.
5. **F3 — Cofre**: A1 e CSC no Vault, validação de titular e validade, alertas de vencimento.
6. **F4 — Perfis fiscais**: TaxProfile, TaxRule e TaxRuleVersion; quatro olhos; vigência; importação do perfil da POC.
7. **F5 — Regulatório**: carga do catálogo, SchemaVersion, resolução por autorizador, ambiente e data; liberação auditada de elementos exclusivos.
8. **Fechamento**: contrato e lint, quickstart de onboarding de dois tenants, retestes do threat model na stack local, checklist B atualizado, sign-off.

## Acceptance Gates

| Gate | Evidência |
| --- | --- |
| Isolamento | testes cruzados entre dois tenants na API e no banco (RLS e chaves compostas) |
| Numeração | 1.000 alocações concorrentes sem repetição nem decremento; lacunas auditadas |
| Segredos | canários ausentes de respostas, logs, traces, dump do banco e camadas da imagem |
| Quatro olhos | autor não aprova; aprovação auditada; versão aprovada imutável |
| Schema | resolução SP/homologação/2026-11-09 devolve 010g com exclusivos desligados |
| Contrato | lint sem achados altos; testes de contrato passam |
| Imagem | chiseled, não-root, fuso SP, SBOM CycloneDX e scan sem alto/crítico com correção disponível |
| Reprodutibilidade | máquina limpa: `docker compose up` e a suíte completa passam |

## Risks

- **Emissor de token próprio** é superfície sensível: limitar a `client_credentials`, testar expiração, rotação e chave comprometida; reavaliar IdP externo no ADR-013.
- **Vault de desenvolvimento** não prova IAM nem exposição em memória em nuvem (L1): o reteste no ambiente escolhido segue no checklist B.
- **Escopo da Fase 1 crescer para emissão**: qualquer chamada à SEFAZ fica fora desta branch.
- **Contador indisponível** (T011): o perfil fica em rascunho; a fundação fecha sem perfil aprovado real, usando um perfil de teste aprovado por credencial de teste.
