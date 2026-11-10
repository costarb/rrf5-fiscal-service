# Feature Specification: RRF5 Fiscal Service — Fundação

**Feature Branch**: `feature/002-fiscal-foundation`

**Created**: 2026-11-09

**Status**: Draft para revisão do operador

**Input**: Fase 1 do roadmap (`docs/fiscal-service/14-implementation-roadmap.md`) com as decisões da discovery (ADR-011, ADR-012, `discovery-signoff.md`) e as escolhas do operador de 2026-11-09: execução local primeiro, administração só por API, papel de aprovador fiscal e perfil fiscal dentro da fundação.

Esta feature implementa, em código de produto, a base sobre a qual a emissão (Fase 2) vai rodar. **Ela não emite documento fiscal.** O produto completo continua descrito em [`specs/001-fiscal-service/spec.md`](../001-fiscal-service/spec.md); aqui ficam só o recorte e os critérios da fundação.

## User Scenarios & Testing

### User Story F1 — Onboarding isolado de tenants e aplicações (Priority: P1)

Um administrador da plataforma cria um tenant e uma aplicação cliente com scopes. A aplicação obtém um token máquina-a-máquina e só enxerga os recursos do próprio tenant e dos estabelecimentos concedidos.

**Independent Test**: criar dois tenants, cada um com uma aplicação; provar que nenhuma chamada de um alcança dados do outro, nem por ID adivinhado.

**Acceptance Scenarios**:

1. **Given** dois tenants, **When** a aplicação A consulta um recurso do tenant B pelo ID, **Then** a resposta é `404`, sem vazar a existência do recurso.
2. **Given** um token sem o scope exigido, **When** chama a operação, **Then** recebe `403` com problema tipado.
3. **Given** o segredo de uma aplicação, **When** ele é rotacionado, **Then** o segredo antigo deixa de emitir token e nunca pode ser recuperado.

### User Story F2 — Cadastro fiscal do emitente (Priority: P1)

Com scope administrativo, a aplicação cadastra empresa, estabelecimento, ambiente (homologação ou produção) e séries. A numeração é reservada de forma atômica, sem lacuna silenciosa e sem mistura de ambiente.

**Independent Test**: cadastrar o emitente sintético em homologação; disputar a mesma série com alocadores concorrentes e provar números distintos e monotônicos.

**Acceptance Scenarios**:

1. **Given** um estabelecimento com série em homologação, **When** dois alocadores pedem número ao mesmo tempo, **Then** recebem números distintos e cada reserva fica auditada.
2. **Given** uma série de homologação, **When** um recurso de produção tenta usá-la, **Then** o banco rejeita a ligação.
3. **Given** um número reservado e não usado, **When** a reserva expira ou falha, **Then** ele vira lacuna auditável e o contador nunca decresce.

### User Story F3 — Cofre de certificado A1 e CSC (Priority: P1)

O administrador envia o A1 (PKCS#12 e senha) e o CSC do estabelecimento. O serviço valida titularidade e validade, guarda o material cifrado no cofre e nunca o devolve.

**Independent Test**: enviar um certificado descartável "NOT AN A1" com CNPJ diferente do estabelecimento e um com CNPJ igual; o primeiro é recusado, o segundo é aceito. Uma senha canário não aparece em resposta, log, trace, banco nem camada de imagem.

**Acceptance Scenarios**:

1. **Given** um A1 cujo CNPJ do titular difere do estabelecimento, **When** é enviado, **Then** é recusado antes de ser guardado.
2. **Given** um A1 aceito, **When** qualquer operação de leitura é chamada, **Then** só aparecem metadados (fingerprint, titular, validade e estado), nunca bytes nem senha.
3. **Given** um certificado a 60, 30, 15, 7 ou 1 dia do vencimento, **When** o job diário roda, **Then** um alerta é emitido (SC-007 da 001).

### User Story F4 — Perfis fiscais versionados com aprovação (Priority: P1)

Um autor cadastra perfis fiscais e regras com dimensões, vigência e referência de fonte. Um aprovador fiscal diferente do autor aprova a versão. Só versões aprovadas e vigentes podem ser resolvidas.

**Independent Test**: importar o perfil da POC `SP-SN-OWN-PRODUCTION-V1` como rascunho; tentar aprovar com o próprio autor (recusado) e com o aprovador (aceito); resolver o perfil por data dentro e fora da vigência.

**Acceptance Scenarios**:

1. **Given** uma versão em rascunho, **When** o próprio autor tenta aprová-la, **Then** a operação é recusada (quatro olhos).
2. **Given** uma versão aprovada, **When** alguém tenta alterá-la, **Then** a alteração é recusada; mudança exige nova versão.
3. **Given** duas versões com as mesmas dimensões, **When** as vigências se sobrepõem, **Then** a segunda não pode ser aprovada.
4. **Given** uma regra que pede um grupo do leiaute que o código não sabe montar, **When** é enviada, **Then** é recusada com o motivo, sem inferir tributação.

### User Story F5 — Catálogo normativo e seleção de schema (Priority: P2)

O serviço carrega o catálogo oficial (`regulatory/catalog.json`) e resolve a versão de schema por autorizador, ambiente e data. Elementos exclusivos de um pacote só são liberados por configuração explícita.

**Independent Test**: para SEFAZ-SP homologação em 2026-11-09, a resolução devolve 010g v1.03 com os elementos exclusivos do 010g **desligados** (E4); ligar a configuração exige registro auditado.

### Edge Cases

- Token válido de um tenant usado com ID de estabelecimento de outro tenant.
- Duas instâncias da API pedindo número da mesma série no mesmo milissegundo.
- Rotação de certificado enquanto outro processo lê os metadados do anterior.
- Versão de regra aprovada com vigência retroativa a documentos já existentes (proibido).
- Mesmo `Idempotency-Key` com payload diferente em operação administrativa.
- Cofre indisponível: cadastro de certificado falha fechado, sem gravar parcial.
- Pacote de schema novo publicado sem ZIP com hash: não pode ser ativado.

## Requirements

### Functional Requirements

- **FF-001**: Toda operação administrativa MUST exigir token OAuth 2.0 client credentials com scope específico; tokens são curtos e assinados.
- **FF-002**: Todo recurso MUST pertencer a um tenant, e o isolamento MUST ser garantido por chaves compostas e RLS, não só por filtro de aplicação (`data-model.md` da 001).
- **FF-003**: Toda mutação administrativa MUST aceitar `Idempotency-Key` com hash do payload; mesmo hash devolve o mesmo resultado, hash diferente devolve conflito.
- **FF-004**: Ambientes de homologação e produção MUST ter séries, certificados, CSC e configuração separados, com mistura rejeitada pelo banco.
- **FF-005**: A alocação de número MUST ser atômica, monotônica e sem caminho de decremento.
- **FF-006**: A1 e CSC MUST ser guardados só no cofre por envelope encryption; nenhum endpoint os devolve; titular e validade são verificados no envio.
- **FF-007**: Perfis e regras fiscais MUST ter autoria, vigência, referência de fonte, versão imutável e aprovação por principal diferente do autor.
- **FF-008**: A validação de regra MUST checar consistência e suporte do gerador; o serviço MUST NOT decidir classificação fiscal.
- **FF-009**: A versão de schema MUST ser resolvida por autorizador, ambiente e data a partir do catálogo com hash; elementos exclusivos de pacote só com liberação auditada.
- **FF-010**: Toda ação administrativa MUST gerar registro de auditoria append-only com principal, tenant, recurso, ação e correlação.
- **FF-011**: Logs, traces e respostas MUST passar por redação; nenhum segredo, chave de acesso de documento real ou senha aparece neles.
- **FF-012**: Erros MUST seguir Problem Details com códigos de domínio estáveis.
- **FF-013**: O contrato OpenAPI MUST incluir as operações administrativas, `401`/`403`/`429`, o enum fechado de estado do documento (L10) e passar no lint sem achados altos (L11).
- **FF-014**: A imagem MUST usar a base chiseled do .NET 10, rodar como não-root, ter fuso `America/Sao_Paulo` funcional e passar por SBOM e scan no CI (L2, L3, ADR-012).
- **FF-015**: A stack local (PostgreSQL, storage S3-compatível, fila e OpenBao) MUST subir por um comando e ser a mesma usada nos testes de integração.

### Key Entities

Usa as entidades de `specs/001-fiscal-service/data-model.md`: Tenant, Application, Company, Establishment, FiscalEnvironment, FiscalConfiguration, FiscalSeries, NumberReservation, DigitalCertificate, TaxProfile, TaxRule, TaxRuleVersion, IdempotencyRecord, AuditLog e SchemaVersion. `FiscalDocument` entra só como tabela e restrições, sem fluxo de emissão.

## Success Criteria

- **SF-001**: Dois tenants concluem o onboarding (aplicação, empresa, estabelecimento, ambiente, série, certificado descartável, CSC sintético e perfil aprovado) usando só a API e o quickstart.
- **SF-002**: 100% dos testes cruzados entre tenants falham fechado (`404`/`403` ou violação de restrição).
- **SF-003**: Em 1.000 alocações concorrentes na mesma série, nenhum número repete e nenhum decresce.
- **SF-004**: Senha canário do A1 e CSC canário ausentes de respostas, logs, traces, dump do banco e camadas da imagem.
- **SF-005**: Nenhuma versão de regra é aprovada pelo próprio autor; toda aprovação aparece na auditoria.
- **SF-006**: `docker compose up` mais um comando de teste reproduzem toda a suíte em máquina limpa.

## Out of Scope

Emissão, assinatura e transmissão de documento (Fase 2); DANFE; cancelamento e inutilização; webhooks; contingência; painel (FR-023); produção; escolha do provedor de nuvem (decidida antes da primeira emissão em homologação, checklist B).

## Assumptions

- O operador é o administrador da plataforma; o contador recebe a credencial de aprovador fiscal.
- Credenciais humanas e login no painel ficam para depois; nesta fase todo principal é uma aplicação OAuth.
- O perfil importado da POC entra como rascunho e só é aprovado depois da ratificação do contador (T011 da 001).
