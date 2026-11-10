# Feature Specification: RRF5 Fiscal Service

**Feature Branch**: `001-fiscal-service`

**Created**: 2026-08-04

**Status**: Draft para validação fiscal, arquitetural e de produto

**Input**: Serviço fiscal independente e multiempresa para abstrair NF-e/NFC-e, inicialmente integrado ao BurgoOS.

## User Scenarios & Testing

### User Story 1 - Emitir NFC-e de venda presencial (Priority: P1)

Um sistema comercial envia dados normalizados de uma venda e recebe uma NFC-e autorizada, ou uma rejeição fiscal explicada, sem conhecer XML, assinatura ou protocolos estaduais.

**Why this priority**: É o fluxo obrigatório para operar o piloto varejista em São Paulo.

**Independent Test**: Cadastrar emitente, estabelecimento, A1, CSC, série e perfil fiscal em homologação; emitir uma venda simples e validar chave, protocolo, XML processado e DANFE.

**Acceptance Scenarios**:

1. **Given** configuração fiscal válida, **When** uma venda idempotente é enviada, **Then** o documento autorizado pode ser consultado e baixado.
2. **Given** uma rejeição do autorizador, **When** a emissão termina, **Then** o cliente recebe estado e código fiscal próprios, sem erro genérico de servidor.
3. **Given** a mesma chave idempotente e payload, **When** a chamada é repetida, **Then** nenhuma segunda nota é criada.

### User Story 2 - Operar indisponibilidade com segurança (Priority: P1)

O estabelecimento continua a operação mediante contingência oficial aplicável e reconcilia documentos posteriormente, sem duplicidade ou perda de rastreabilidade.

**Why this priority**: Food trucks e balcões não podem depender de conectividade contínua, mas a contingência precisa cumprir o fluxo fiscal.

**Independent Test**: Simular perda de conexão antes e depois da transmissão, gerar documento em contingência offline NFC-e, restabelecer conexão e reconciliar o resultado.

**Acceptance Scenarios**:

1. **Given** indisponibilidade confirmada, **When** a contingência é habilitada, **Then** o documento registra modalidade, justificativa, horário e prazo operacional.
2. **Given** resultado incerto após envio, **When** o serviço recupera, **Then** consulta recibo/chave antes de qualquer retransmissão.
3. **Given** rejeição posterior de documento impresso em contingência, **When** ocorre reconciliação, **Then** uma exceção operacional bloqueante é aberta e auditada.

### User Story 3 - Administrar configuração fiscal multiempresa (Priority: P1)

Um responsável autorizado cadastra empresas, estabelecimentos, certificados, CSC, séries e perfis fiscais com separação de ambiente e tenant.

**Why this priority**: Nenhuma emissão é segura sem configuração, isolamento e governança.

**Independent Test**: Configurar dois tenants e provar que credenciais, documentos, numeração e certificados nunca se cruzam.

**Acceptance Scenarios**:

1. **Given** dois tenants, **When** uma aplicação consulta ou emite, **Then** enxerga apenas estabelecimentos autorizados.
2. **Given** certificado vencido ou incompatível com o emitente, **When** a emissão é solicitada, **Then** ela é bloqueada antes da transmissão.
3. **Given** configuração de homologação, **When** produção é solicitada, **Then** o serviço rejeita a mistura de ambientes.

### User Story 4 - Cancelar, inutilizar e consultar (Priority: P1)

O integrador consulta o estado final e solicita cancelamento ou inutilização conforme as regras vigentes, sempre com justificativa e auditoria.

**Independent Test**: Autorizar e cancelar uma NFC-e; criar uma lacuna controlada e inutilizá-la; verificar XMLs e protocolos dos eventos.

**Acceptance Scenarios**:

1. **Given** documento cancelável, **When** usuário autorizado informa justificativa, **Then** o evento protocolado fica imutável e consultável.
2. **Given** prazo ou estado incompatível, **When** cancelamento é solicitado, **Then** a API responde erro de domínio sem alterar o documento.
3. **Given** faixa não utilizada, **When** a inutilização é autorizada, **Then** os números não podem ser emitidos depois.

### User Story 5 - Integrar sistemas terceiros (Priority: P2)

Uma aplicação obtém credenciais, pré-valida documentos, emite de forma síncrona ou assíncrona, consulta e consome webhooks assinados.

**Why this priority**: O produto precisa ser reutilizável além do BurgoOS.

**Independent Test**: Integrar uma aplicação sandbox apenas com documentação pública e concluir a primeira emissão em homologação.

**Acceptance Scenarios**:

1. **Given** credencial com escopo limitado, **When** usa a API, **Then** somente operações e estabelecimentos concedidos são aceitos.
2. **Given** webhook duplicado ou fora de ordem, **When** o cliente o processa, **Then** consegue confirmar o estado pela consulta da API.

### User Story 6 - Evoluir para NF-e e escala nacional (Priority: P3)

O produto adiciona modelo 55 e novos autorizadores sem quebrar contratos NFC-e existentes.

**Why this priority**: É expansão estratégica, posterior à validação do piloto NFC-e/SP.

**Independent Test**: Habilitar adaptador de modelo/UF em sandbox e executar casos fiscais versionados sem alterar o contrato comercial básico.

### Edge Cases

- Autorização confirmada pela SEFAZ seguida de timeout local.
- Duas instâncias disputando o mesmo número/série.
- Chave idempotente reutilizada com payload diferente.
- Certificado renovado durante uma emissão em processamento.
- CSC incorreto, série migrada de emissor anterior ou lacuna de numeração.
- Falha depois da impressão em contingência e rejeição na transmissão posterior.
- Pagamento misto, troco, desconto, acréscimo, delivery, marketplace, combo e adicionais.
- Mudança de schema/regra com documentos antigos ainda consultáveis.
- CPF e endereço sujeitos à LGPD, porém documento sob retenção fiscal obrigatória.

## Requirements

### Functional Requirements

- **FR-001**: O produto MUST ser independente do BurgoOS e expor contratos versionados para aplicações autorizadas.
- **FR-002**: O MVP MUST suportar NFC-e modelo 65 em São Paulo, A1, homologação e produção.
- **FR-003**: O produto MUST aceitar dados comerciais normalizados e resolver configuração fiscal versionada.
- **FR-004**: O produto MUST gerar, validar, assinar, transmitir, interpretar, protocolar e armazenar documentos conforme artefatos oficiais vigentes.
- **FR-005**: O produto MUST oferecer emissão síncrona, assíncrona e pré-validação sem reserva definitiva de número nesta última.
- **FR-006**: Toda mutação fiscal MUST ser idempotente por tenant, estabelecimento, operação e chave, com hash do payload.
- **FR-007**: O produto MUST consultar o autorizador antes de retransmitir quando o resultado anterior for incerto.
- **FR-008**: Numeração MUST ser exclusiva por CNPJ/estabelecimento, modelo, série e ambiente, com concorrência serializada e auditoria de lacunas.
- **FR-009**: Certificados e senhas MUST permanecer cifrados, segregados e indisponíveis para exportação por usuários comuns.
- **FR-010**: XML autorizado, protocolo e eventos MUST ser imutáveis, íntegros, versionados e recuperáveis.
- **FR-011**: O produto MUST gerar DANFE/QR Code com a versão aplicável ao documento.
- **FR-012**: O produto MUST suportar consulta, cancelamento, inutilização e download no MVP.
- **FR-013**: Contingência MUST seguir a modalidade oficialmente aplicável; simples fila de reenvio não é contingência fiscal.
- **FR-014**: Webhooks MUST ser assinados, repetíveis, auditáveis e nunca constituir a única fonte de estado.
- **FR-015**: Homologação e produção MUST separar credenciais, CSC, séries, números, documentos, storage e observabilidade.
- **FR-016**: Perfis e regras tributárias MUST ter autoria, vigência e versão; documentos preservam a versão utilizada.
- **FR-017**: NCM, CEST, CFOP, CST, CSOSN, alíquotas, benefícios, monofásico, IBS e CBS MUST exigir validação fiscal/contábil.
- **FR-018**: O produto MUST manter catálogo versionado de schemas, endpoints, notas técnicas e datas de vigência.
- **FR-019**: A API MUST usar autenticação máquina-a-máquina, scopes e autorização por tenant/estabelecimento.
- **FR-020**: Ações críticas MUST exigir permissão, confirmação, justificativa e trilha de auditoria.
- **FR-021**: O produto MUST apresentar rejeição SEFAZ separada de falha técnica, indisponibilidade, cancelamento e inutilização.
- **FR-022**: O produto MUST suportar pagamentos simples, mistos, troco e dados de credenciadora quando exigidos pela regra vigente.
- **FR-023**: O painel MUST permitir operar cadastros, certificados, séries, documentos, eventos, contingências, webhooks e auditoria.
- **FR-024**: O serviço MUST expor health/status sem prometer disponibilidade da SEFAZ como SLA próprio.
- **FR-025**: O desenho MUST permitir NF-e 55 e adaptadores nacionais após o MVP, sem misturar NFS-e no mesmo adaptador.

### Key Entities

- **Tenant/Application**: proprietário lógico e cliente autenticado.
- **Company/Establishment**: emitente e unidade fiscal autorizada.
- **FiscalEnvironment/Configuration**: isolamento e parâmetros por ambiente.
- **DigitalCertificate**: metadados e referência cifrada do A1.
- **FiscalSeries/NumberReservation**: série, próximo número e histórico concorrente.
- **TaxProfile/TaxRuleVersion**: configuração fiscal validada e vigente.
- **FiscalDocument/Item/Payment/Reference**: agregado imutável da emissão.
- **FiscalProtocol/Event/ContingencyRecord**: respostas e eventos oficiais/operacionais.
- **IdempotencyRecord/SefazRequest/SefazResponse**: proteção contra duplicidade e rastreabilidade.
- **WebhookSubscription/Delivery/AuditLog/SchemaVersion**: integração, governança e evidência.

## Success Criteria

### Measurable Outcomes

- **SC-001**: Uma aplicação nova conclui a primeira NFC-e em homologação em até 1 dia usando apenas o material de integração.
- **SC-002**: 100% das repetições idempotentes do mesmo payload retornam o mesmo documento sem nova numeração.
- **SC-003**: Nenhum teste de isolamento entre dois tenants acessa certificado, documento, série ou configuração do outro.
- **SC-004**: 100% dos documentos autorizados possuem XML processado, protocolo, verificação de integridade e DANFE recuperáveis.
- **SC-005**: Em resultado incerto, nenhuma retentativa transmite antes de consultar recibo/chave.
- **SC-006**: 95% das solicitações são aceitas ou rejeitadas localmente em até 500 ms, excluindo tempo externo da SEFAZ.
- **SC-007**: Alertas de certificado são emitidos em 60, 30, 15, 7 e 1 dia antes do vencimento.
- **SC-008**: O piloto passa por venda, cancelamento, inutilização, indisponibilidade e reconciliação de contingência em homologação antes da produção.

## Assumptions

- O RRF5 Fiscal Service será um novo repositório/produto; estes documentos permanecem inicialmente no BurgoOS como decisão e contrato de origem.
- Primeira implantação: NFC-e/SP, leiaute 4.00, certificado A1; versões concretas serão resolvidas por vigência.
- O MVP é monólito modular com API e workers implantáveis separadamente, banco e storage próprios.
- NF-e 55, NFS-e, A3, SDKs completos e escala nacional ficam fora do primeiro piloto.
- Um contador/responsável fiscal aprovará perfis antes de produção.
- Retenção fiscal será configurada conforme obrigação legal aplicável; exclusão LGPD não apagará documentos cuja guarda seja obrigatória.
