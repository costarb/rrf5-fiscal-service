# Sign-off: Fiscal Service Foundation (feature 002)

**Status:** APROVADO pelo operador em 2026-11-11  
**Preparado em:** 2026-11-11  
**Branch:** `feature/002-fiscal-foundation`

A fundação entrega identidade multi-tenant, cadastro do emitente, numeração
atômica, cofre de A1/CSC, perfis fiscais com quatro olhos, catálogo normativo
com seleção de schema, auditoria e observabilidade com redação, tudo sobre a
stack local. **Não emite documento fiscal.**

## Acceptance gates do plano

| Gate | Resultado | Evidência |
| --- | --- | --- |
| Isolamento | Passa | API: `CrossTenantTests`, `IssuerIsolationTests`, `TaxRuleLifecycleTests`, `SchemaSelectionApiTests`, `TwoTenantOnboardingTests` (404 em todo recurso de outro tenant ou estabelecimento não concedido). Banco: `RowLevelSecurityTests` (RLS forçado, contexto ausente, estrangeiro ou malformado negado) e `ConstraintTests` (chaves compostas) |
| Numeração | Passa | `NumberAllocationTests`: 1.000 alocações concorrentes de duas instâncias, números 1–1.000 sem repetição, contador em 1.001; lacuna auditada e nunca reaproveitada |
| Segredos | Passa (local) | `SecretCanaryTests`: senha do A1 e CSC canários ausentes de respostas, logs, spans, todas as tabelas e entradas do cofre (verificado por mutação); camadas das imagens varridas no CI por `scripts/scan_image_layers.py` |
| Quatro olhos | Passa | Autor e aprovador são aplicações diferentes (scope e banco); versão aprovada imutável; toda transição auditada com o principal (`TaxRuleLifecycleTests`, `TaxRuleConstraintTests`) |
| Schema | Passa | SP/homologação/2026-11-09 resolve 010g v1.03 com todos os exclusivos desligados; produção na mesma data resolve 010f (`SchemaSelectionApiTests`, `SchemaSelectionTests`) |
| Contrato | Passa | `specs/002-fiscal-foundation/contracts/openapi.yaml` (OpenAPI 3.1) sem achados no Redocly 2.71.2/Node 22; rotas, códigos de erro, scopes e estados iguais ao código (`ContractTests`) |
| Imagem | Passa | Chiseled `aspnet:10.0-noble-chiseled-extra`, UID 1654, fuso SP; SBOM CycloneDX e Trivy sem alto/crítico com correção, a cada push |
| Reprodutibilidade | Passa | Clone novo e stack nova (projeto compose separado, outras portas, volumes vazios) em 2026-11-11: quickstart, 9 migrações e 191 testes verdes; CI verde em runner Linux novo |

## Critérios de sucesso (spec)

| Critério | Resultado |
| --- | --- |
| SF-001 Dois tenants em onboarding completo só por API e quickstart | Passa (`TwoTenantOnboardingTests`) |
| SF-002 100% dos testes cruzados falham fechado | Passa |
| SF-003 1.000 alocações sem repetição nem decremento | Passa |
| SF-004 Canários ausentes de respostas, logs, traces, banco e imagem | Passa na stack local |
| SF-005 Nenhuma versão aprovada pelo próprio autor; aprovações auditadas | Passa |
| SF-006 `docker compose up` e a suíte reproduzem em máquina limpa | Passa em clone e stack limpos nesta máquina e no CI |

## Decisões tomadas durante a feature

- ADR-013, ADR-014 e ADR-015 aceitos pelo operador em 2026-11-10.
- ADR-002, ADR-004, ADR-008 e ADR-009 marcados "Aceito com evidência da
  fundação", sujeitos a este sign-off.
- SeaweedFS no lugar do MinIO e OpenBao no lugar do Vault (imagens e licença).
- A1 aceito quando o CNPJ-Base do titular é igual ao do estabelecimento (MOC
  Anexo I, regra F03, rejeição 213). O titular vem do OID ICP-Brasil 2.16.76.1.3.3.
- Lista de elementos exclusivos de cada pacote obtida do diff dos XSDs oficiais
  sobre o 010e (D7), recalculada por teste quando os XSDs estão na máquina.
- Dependências novas registradas no gate: Npgsql 10.0.3, OpenTelemetry 1.20.1,
  JwtBearer 10.0.10, Microsoft.Extensions.*.Abstractions 10.0.10, AWSSDK.S3
  4.0.104.7 e Redocly CLI 2.71.2 (só desenvolvimento).

## Itens abertos

| Item | Onde | Bloqueia |
| --- | --- | --- |
| Ratificação do perfil `SP-SN-OWN-PRODUCTION-V1` pelo contador; o perfil importado segue em DRAFT | T011 da 001, checklist C | Aprovação do perfil real |
| Sonda do 010g depois de 2026-11-16 na homologação SP antes de liberar qualquer exclusivo | Checklist B | Elementos exclusivos do 010g |
| Validação da cadeia ICP-Brasil do A1 e rotação com controle duplo | Threat model TM-03, ADR-004 | Produção |
| Política de acesso do cofre por identidade (hoje token raiz do dev) e bucket privado | TM-01, TM-08, checklist B | Implantação em nuvem |
| Itens de emissão do checklist B: golden XML 010g, assinatura (L8), DANFE (L5, selftest), arredondamento, raízes TLS por autorizador, exemplos de polling | Checklist B | Primeira emissão em homologação |
| Escolha do provedor de nuvem e reteste do threat model nele | Plano D3, checklist B | Primeira emissão em homologação |
| Servicing do ASP.NET Core (10.0.10 em uso, 10.0.15 disponível) | Gate de dependências | — |

## Aprovação

| Papel | Nome | Data | Decisão |
| --- | --- | --- | --- |
| Operador e proprietário do emitente | Operador (conta `costarb`) | 2026-11-11 | Aprovado ("de acordo"), com os itens abertos acima mantidos nos checklists B e C e no T011 da 001 (retorno do contador previsto até 2026-11-13) |
