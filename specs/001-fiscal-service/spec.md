# Feature Specification: RRF5 Fiscal Service

**Feature Branch**: `001-fiscal-service`

**Created**: 2026-08-04

**Status**: Draft para validaÃ§Ã£o fiscal, arquitetural e de produto

**Input**: ServiÃ§o fiscal independente e multiempresa para abstrair NF-e/NFC-e, inicialmente integrado ao BurgoOS.

## User Scenarios & Testing

### User Story 1 - Emitir NFC-e de venda presencial (Priority: P1)

Um sistema comercial envia dados normalizados de uma venda e recebe uma NFC-e autorizada, ou uma rejeiÃ§Ã£o fiscal explicada, sem conhecer XML, assinatura ou protocolos estaduais.

**Why this priority**: Ã‰ o fluxo obrigatÃ³rio para operar o piloto varejista em SÃ£o Paulo.

**Independent Test**: Cadastrar emitente, estabelecimento, A1, CSC, sÃ©rie e perfil fiscal em homologaÃ§Ã£o; emitir uma venda simples e validar chave, protocolo, XML processado e DANFE.

**Acceptance Scenarios**:

1. **Given** configuraÃ§Ã£o fiscal vÃ¡lida, **When** uma venda idempotente Ã© enviada, **Then** o documento autorizado pode ser consultado e baixado.
2. **Given** uma rejeiÃ§Ã£o do autorizador, **When** a emissÃ£o termina, **Then** o cliente recebe estado e cÃ³digo fiscal prÃ³prios, sem erro genÃ©rico de servidor.
3. **Given** a mesma chave idempotente e payload, **When** a chamada Ã© repetida, **Then** nenhuma segunda nota Ã© criada.

### User Story 2 - Operar indisponibilidade com seguranÃ§a (Priority: P1)

O estabelecimento continua a operaÃ§Ã£o mediante contingÃªncia oficial aplicÃ¡vel e reconcilia documentos posteriormente, sem duplicidade ou perda de rastreabilidade.

**Why this priority**: Food trucks e balcÃµes nÃ£o podem depender de conectividade contÃ­nua, mas a contingÃªncia precisa cumprir o fluxo fiscal.

**Independent Test**: Simular perda de conexÃ£o antes e depois da transmissÃ£o, gerar documento em contingÃªncia offline NFC-e, restabelecer conexÃ£o e reconciliar o resultado.

**Acceptance Scenarios**:

1. **Given** indisponibilidade confirmada, **When** a contingÃªncia Ã© habilitada, **Then** o documento registra modalidade, justificativa, horÃ¡rio e prazo operacional.
2. **Given** resultado incerto apÃ³s envio, **When** o serviÃ§o recupera, **Then** consulta recibo/chave antes de qualquer retransmissÃ£o.
3. **Given** rejeiÃ§Ã£o posterior de documento impresso em contingÃªncia, **When** ocorre reconciliaÃ§Ã£o, **Then** uma exceÃ§Ã£o operacional bloqueante Ã© aberta e auditada.

### User Story 3 - Administrar configuraÃ§Ã£o fiscal multiempresa (Priority: P1)

Um responsÃ¡vel autorizado cadastra empresas, estabelecimentos, certificados, CSC, sÃ©ries e perfis fiscais com separaÃ§Ã£o de ambiente e tenant.

**Why this priority**: Nenhuma emissÃ£o Ã© segura sem configuraÃ§Ã£o, isolamento e governanÃ§a.

**Independent Test**: Configurar dois tenants e provar que credenciais, documentos, numeraÃ§Ã£o e certificados nunca se cruzam.

**Acceptance Scenarios**:

1. **Given** dois tenants, **When** uma aplicaÃ§Ã£o consulta ou emite, **Then** enxerga apenas estabelecimentos autorizados.
2. **Given** certificado vencido ou incompatÃ­vel com o emitente, **When** a emissÃ£o Ã© solicitada, **Then** ela Ã© bloqueada antes da transmissÃ£o.
3. **Given** configuraÃ§Ã£o de homologaÃ§Ã£o, **When** produÃ§Ã£o Ã© solicitada, **Then** o serviÃ§o rejeita a mistura de ambientes.

### User Story 4 - Cancelar, inutilizar e consultar (Priority: P1)

O integrador consulta o estado final e solicita cancelamento ou inutilizaÃ§Ã£o conforme as regras vigentes, sempre com justificativa e auditoria.

**Independent Test**: Autorizar e cancelar uma NFC-e; criar uma lacuna controlada e inutilizÃ¡-la; verificar XMLs e protocolos dos eventos.

**Acceptance Scenarios**:

1. **Given** documento cancelÃ¡vel, **When** usuÃ¡rio autorizado informa justificativa, **Then** o evento protocolado fica imutÃ¡vel e consultÃ¡vel.
2. **Given** prazo ou estado incompatÃ­vel, **When** cancelamento Ã© solicitado, **Then** a API responde erro de domÃ­nio sem alterar o documento.
3. **Given** faixa nÃ£o utilizada, **When** a inutilizaÃ§Ã£o Ã© autorizada, **Then** os nÃºmeros nÃ£o podem ser emitidos depois.

### User Story 5 - Integrar sistemas terceiros (Priority: P2)

Uma aplicaÃ§Ã£o obtÃ©m credenciais, prÃ©-valida documentos, emite de forma sÃ­ncrona ou assÃ­ncrona, consulta e consome webhooks assinados.

**Why this priority**: O produto precisa ser reutilizÃ¡vel alÃ©m do BurgoOS.

**Independent Test**: Integrar uma aplicaÃ§Ã£o sandbox apenas com documentaÃ§Ã£o pÃºblica e concluir a primeira emissÃ£o em homologaÃ§Ã£o.

**Acceptance Scenarios**:

1. **Given** credencial com escopo limitado, **When** usa a API, **Then** somente operaÃ§Ãµes e estabelecimentos concedidos sÃ£o aceitos.
2. **Given** webhook duplicado ou fora de ordem, **When** o cliente o processa, **Then** consegue confirmar o estado pela consulta da API.

### User Story 6 - Evoluir para NF-e e escala nacional (Priority: P3)

O produto adiciona modelo 55 e novos autorizadores sem quebrar contratos NFC-e existentes.

**Why this priority**: Ã‰ expansÃ£o estratÃ©gica, posterior Ã  validaÃ§Ã£o do piloto NFC-e/SP.

**Independent Test**: Habilitar adaptador de modelo/UF em sandbox e executar casos fiscais versionados sem alterar o contrato comercial bÃ¡sico.

### Edge Cases

- AutorizaÃ§Ã£o confirmada pela SEFAZ seguida de timeout local.
- Duas instÃ¢ncias disputando o mesmo nÃºmero/sÃ©rie.
- Chave idempotente reutilizada com payload diferente.
- Certificado renovado durante uma emissÃ£o em processamento.
- CSC incorreto, sÃ©rie migrada de emissor anterior ou lacuna de numeraÃ§Ã£o.
- Falha depois da impressÃ£o em contingÃªncia e rejeiÃ§Ã£o na transmissÃ£o posterior.
- Pagamento misto, troco, desconto, acrÃ©scimo, delivery, marketplace, combo e adicionais.
- MudanÃ§a de schema/regra com documentos antigos ainda consultÃ¡veis.
- CPF e endereÃ§o sujeitos Ã  LGPD, porÃ©m documento sob retenÃ§Ã£o fiscal obrigatÃ³ria.

## Requirements

### Functional Requirements

- **FR-001**: O produto MUST ser independente do BurgoOS e expor contratos versionados para aplicaÃ§Ãµes autorizadas.
- **FR-002**: O MVP MUST suportar NFC-e modelo 65 em SÃ£o Paulo, A1, homologaÃ§Ã£o e produÃ§Ã£o.
- **FR-003**: O produto MUST aceitar dados comerciais normalizados e resolver configuraÃ§Ã£o fiscal versionada.
- **FR-004**: O produto MUST gerar, validar, assinar, transmitir, interpretar, protocolar e armazenar documentos conforme artefatos oficiais vigentes.
- **FR-005**: O produto MUST oferecer emissÃ£o sÃ­ncrona, assÃ­ncrona e prÃ©-validaÃ§Ã£o sem reserva definitiva de nÃºmero nesta Ãºltima.
- **FR-006**: Toda mutaÃ§Ã£o fiscal MUST ser idempotente por tenant, estabelecimento, operaÃ§Ã£o e chave, com hash do payload.
- **FR-007**: O produto MUST consultar o autorizador antes de retransmitir quando o resultado anterior for incerto.
- **FR-008**: NumeraÃ§Ã£o MUST ser exclusiva por CNPJ/estabelecimento, modelo, sÃ©rie e ambiente, com concorrÃªncia serializada e auditoria de lacunas.
- **FR-009**: Certificados e senhas MUST permanecer cifrados, segregados e indisponÃ­veis para exportaÃ§Ã£o por usuÃ¡rios comuns.
- **FR-010**: XML autorizado, protocolo e eventos MUST ser imutÃ¡veis, Ã­ntegros, versionados e recuperÃ¡veis.
- **FR-011**: O produto MUST gerar DANFE/QR Code com a versÃ£o aplicÃ¡vel ao documento.
- **FR-012**: O produto MUST suportar consulta, cancelamento, inutilizaÃ§Ã£o e download no MVP.
- **FR-013**: ContingÃªncia MUST seguir a modalidade oficialmente aplicÃ¡vel; simples fila de reenvio nÃ£o Ã© contingÃªncia fiscal.
- **FR-014**: Webhooks MUST ser assinados, repetÃ­veis, auditÃ¡veis e nunca constituir a Ãºnica fonte de estado.
- **FR-015**: HomologaÃ§Ã£o e produÃ§Ã£o MUST separar credenciais, CSC, sÃ©ries, nÃºmeros, documentos, storage e observabilidade.
- **FR-016**: Perfis e regras tributÃ¡rias MUST ter autoria, vigÃªncia e versÃ£o; documentos preservam a versÃ£o utilizada.
- **FR-017**: NCM, CEST, CFOP, CST, CSOSN, alÃ­quotas, benefÃ­cios, monofÃ¡sico, IBS e CBS MUST exigir validaÃ§Ã£o fiscal/contÃ¡bil.
- **FR-018**: O produto MUST manter catÃ¡logo versionado de schemas, endpoints, notas tÃ©cnicas e datas de vigÃªncia.
- **FR-019**: A API MUST usar autenticaÃ§Ã£o mÃ¡quina-a-mÃ¡quina, scopes e autorizaÃ§Ã£o por tenant/estabelecimento.
- **FR-020**: AÃ§Ãµes crÃ­ticas MUST exigir permissÃ£o, confirmaÃ§Ã£o, justificativa e trilha de auditoria.
- **FR-021**: O produto MUST apresentar rejeiÃ§Ã£o SEFAZ separada de falha tÃ©cnica, indisponibilidade, cancelamento e inutilizaÃ§Ã£o.
- **FR-022**: O produto MUST suportar pagamentos simples, mistos, troco e dados de credenciadora quando exigidos pela regra vigente.
- **FR-023**: O painel MUST permitir operar cadastros, certificados, sÃ©ries, documentos, eventos, contingÃªncias, webhooks e auditoria.
- **FR-024**: O serviÃ§o MUST expor health/status sem prometer disponibilidade da SEFAZ como SLA prÃ³prio.
- **FR-025**: O desenho MUST permitir NF-e 55 e adaptadores nacionais apÃ³s o MVP, sem misturar NFS-e no mesmo adaptador.

### Key Entities

- **Tenant/Application**: proprietÃ¡rio lÃ³gico e cliente autenticado.
- **Company/Establishment**: emitente e unidade fiscal autorizada.
- **FiscalEnvironment/Configuration**: isolamento e parÃ¢metros por ambiente.
- **DigitalCertificate**: metadados e referÃªncia cifrada do A1.
- **FiscalSeries/NumberReservation**: sÃ©rie, prÃ³ximo nÃºmero e histÃ³rico concorrente.
- **TaxProfile/TaxRuleVersion**: configuraÃ§Ã£o fiscal validada e vigente.
- **FiscalDocument/Item/Payment/Reference**: agregado imutÃ¡vel da emissÃ£o.
- **FiscalProtocol/Event/ContingencyRecord**: respostas e eventos oficiais/operacionais.
- **IdempotencyRecord/SefazRequest/SefazResponse**: proteÃ§Ã£o contra duplicidade e rastreabilidade.
- **WebhookSubscription/Delivery/AuditLog/SchemaVersion**: integraÃ§Ã£o, governanÃ§a e evidÃªncia.

## Success Criteria

### Measurable Outcomes

- **SC-001**: Uma aplicaÃ§Ã£o nova conclui a primeira NFC-e em homologaÃ§Ã£o em atÃ© 1 dia usando apenas o material de integraÃ§Ã£o.
- **SC-002**: 100% das repetiÃ§Ãµes idempotentes do mesmo payload retornam o mesmo documento sem nova numeraÃ§Ã£o.
- **SC-003**: Nenhum teste de isolamento entre dois tenants acessa certificado, documento, sÃ©rie ou configuraÃ§Ã£o do outro.
- **SC-004**: 100% dos documentos autorizados possuem XML processado, protocolo, verificaÃ§Ã£o de integridade e DANFE recuperÃ¡veis.
- **SC-005**: Em resultado incerto, nenhuma retentativa transmite antes de consultar recibo/chave.
- **SC-006**: 95% das solicitaÃ§Ãµes sÃ£o aceitas ou rejeitadas localmente em atÃ© 500 ms, excluindo tempo externo da SEFAZ.
- **SC-007**: Alertas de certificado sÃ£o emitidos em 60, 30, 15, 7 e 1 dia antes do vencimento.
- **SC-008**: O piloto passa por venda, cancelamento, inutilizaÃ§Ã£o, indisponibilidade e reconciliaÃ§Ã£o de contingÃªncia em homologaÃ§Ã£o antes da produÃ§Ã£o.

## Assumptions

- O RRF5 Fiscal Service serÃ¡ um novo repositÃ³rio/produto; estes documentos permanecem inicialmente no BurgoOS como decisÃ£o e contrato de origem.
- Primeira implantaÃ§Ã£o: NFC-e/SP, leiaute 4.00, certificado A1; versÃµes concretas serÃ£o resolvidas por vigÃªncia.
- O MVP Ã© monÃ³lito modular com API e workers implantÃ¡veis separadamente, banco e storage prÃ³prios.
- NF-e 55, NFS-e, A3, SDKs completos e escala nacional ficam fora do primeiro piloto.
- Um contador/responsÃ¡vel fiscal aprovarÃ¡ perfis antes de produÃ§Ã£o.
- RetenÃ§Ã£o fiscal serÃ¡ configurada conforme obrigaÃ§Ã£o legal aplicÃ¡vel; exclusÃ£o LGPD nÃ£o apagarÃ¡ documentos cuja guarda seja obrigatÃ³ria.
