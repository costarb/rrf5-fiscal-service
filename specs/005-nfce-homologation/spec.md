# Feature Specification: RRF5 Fiscal Service — Emissão NFC-e em homologação

**Feature Branch**: `feature/005-nfce-homologation`

**Created**: 2026-11-12

**Status**: Rascunho com decisões do operador de 2026-11-12

**Input**:
- Fase 2 do roadmap (`docs/fiscal-service/14-implementation-roadmap.md`): pré-validação, perfil fiscal básico, numeração, XML/XSD/assinatura, autorização/consulta, pagamentos, QR Code/DANFE e rejeições. Aceite: a matriz de vendas do food truck é autorizada ou rejeitada como esperado.
- O que a fundação (002) e as features 003 e 004 entregaram.
- O que a discovery provou: rodadas 3 e 4 de `pocs/evidence/homologation.md`, com autorização real `cStat` 100 e consulta.
- Os itens abertos da seção B do checklist `specs/001-fiscal-service/checklists/foundation-entry.md`.

Esta feature leva a emissão para o código de produto, **só em homologação da SEFAZ-SP**. Produção, cancelamento, inutilização, webhooks e contingência ficam para a Fase 3. O produto completo continua em [`specs/001-fiscal-service/spec.md`](../001-fiscal-service/spec.md).

## User Scenarios & Testing

### User Story E1 — Emitir uma venda de balcão em homologação (Priority: P1)

Um sistema comercial envia uma venda normalizada (itens, pagamentos, troco e identificação opcional do consumidor) para um estabelecimento já configurado na fundação. Recebe uma NFC-e autorizada pela homologação da SEFAZ-SP ou uma rejeição explicada. O sistema não precisa conhecer XML, assinatura nem SOAP.

**Independent Test**: com o emitente de homologação configurado (A1 ativo por controle duplo, CSC, série e perfil aprovado), emitir uma venda simples em dinheiro e verificar chave, protocolo, XML processado (`nfeProc`) guardado e QR Code.

**Acceptance Scenarios**:

1. **Given** a configuração válida, **When** a venda é enviada com `Idempotency-Key`, **Then** o documento chega a `AUTHORIZED` só com protocolo `cStat` 100 cujos `chNFe` e `digVal` conferem com o documento enviado (regra da POC, T018).
2. **Given** uma rejeição da SEFAZ, **When** a emissão termina, **Then** o estado é `REJECTED`, com `cStat` e `xMotivo` oficiais separados de falha técnica.
3. **Given** a mesma chave e o mesmo payload, **When** a chamada se repete, **Then** volta o mesmo documento, sem novo número.
4. **Given** a mesma chave com payload diferente, **When** a chamada chega, **Then** a resposta é conflito, sem documento novo.

### User Story E2 — Pré-validar sem consumir número (Priority: P1)

O integrador pré-valida uma venda e recebe todos os problemas de cadastro, perfil, totais e pagamentos, sem reservar número e sem transmitir (FR-005 da 001).

**Independent Test**: pré-validar uma venda com total de pagamentos diferente do total da nota e uma com item sem regra fiscal vigente; as duas voltam com problemas tipados e a série não avança.

### User Story E3 — Resultado incerto sem duplicidade (Priority: P1)

Quando a transmissão termina em timeout ou em resposta ilegível, o serviço consulta a SEFAZ pela chave antes de qualquer reenvio (FR-007, SC-005 da 001).

**Independent Test**: com o mock SOAP da POC, simular uma autorização confirmada na SEFAZ seguida de timeout local. O serviço consulta, encontra o protocolo e finaliza `AUTHORIZED` sem retransmitir. Se a consulta também falhar, o documento continua em `WAITING_SEFAZ` (o estado do contrato fechado para resultado ainda desconhecido), com nova consulta agendada e alerta, sem retransmissão.

### User Story E4 — Matriz de vendas do food truck (Priority: P1)

Os cenários do MVP em `docs/fiscal-service/17-food-service-scenarios.md` têm fixture, XML golden e resultado esperado, e são exercitados contra a homologação SEFAZ-SP:
- balcão, dinheiro com troco, Pix, cartão, pagamento misto;
- consumidor anônimo e CPF na nota;
- desconto por item e total, acréscimo;
- emissão pós-pagamento, retry duplicado e múltiplos caixas na mesma série.

Os cenários 8, 12, 18–22 e 31–40 ficam fora sem decisão fiscal específica.

**Independent Test**: um comando roda a matriz e produz o quadro com o esperado e o obtido (`cStat`) por cenário, sem chave de acesso nem CNPJ real no repositório.

### User Story E5 — QR Code e DANFE NFC-e (Priority: P2)

Todo documento autorizado tem QR Code na versão aplicável e DANFE NFC-e recuperável, gerado dentro do container Linux (`danfe-selftest`, L3).

### Edge Cases

- A1 vence, ou é substituído por outro aprovado, entre a validação e a assinatura.
- O CSC é trocado com documento em processamento.
- O perfil fiscal perde a vigência entre a pré-validação e a emissão.
- Diferença de centavos entre a soma dos itens, os descontos e os pagamentos.
- Rejeição 225 por elemento exclusivo de pacote não aplicado pela SP (E4): o elemento continua desligado até a sonda aceitar.
- Duas instâncias do worker pegando o mesmo documento.
- SEFAZ fora do ar: nenhuma fila técnica é chamada de contingência.

## Requirements

### Functional Requirements

- **FE-001**: A emissão MUST resolver, na data do documento, o emitente, o ambiente, o A1 `ACTIVE`, o CSC, a série, o perfil fiscal aprovado e vigente e o schema pelo catálogo (FF-009). Qualquer item ausente falha fechado, antes de numerar.
- **FE-002**: O XML MUST seguir a ordem e as regras do leiaute vigente e passar na validação XSD do pacote resolvido antes da assinatura. Elementos exclusivos de pacote seguem desligados até liberação auditada (E4, ADR-009).
- **FE-003**: A assinatura XMLDSig MUST usar o algoritmo e as transformações da seção do MOC 7.0 registrada (L8). A assinatura MUST ser validada antes do envio, e só o worker abre o A1 (política `rrf5-worker`, feature 004).
- **FE-004**: Valores e arredondamentos MUST seguir a regra do leiaute, com os testes de borda da POC (checklist B).
- **FE-005**: NCM, CFOP, CSOSN, origem, unidade, CEST e GTIN MUST vir do ERP em cada item. O CFOP e o CSOSN MUST ser iguais aos da regra aprovada e vigente da natureza de operação (exceção por NCM, senão a regra `ANY`); divergência ou falta de regra recusa o item antes de numerar. PIS, COFINS e CRT vêm da regra. O serviço MUST NOT inferir nem completar nenhum desses valores (FR-017 da 001, AGENTS.md, ADR-017).
- **FE-006**: O meio de pagamento comercial MUST ser mapeado para o código oficial `tPag` pela versão vigente. Os rótulos do DANFE vêm da tabela do MOC congelado (L5).
- **FE-007**: A transmissão MUST usar SOAP 1.2 com TLS mútuo ao endpoint do catálogo, com raízes ICP-Brasil fixadas por autorizador e validação TLS sempre ligada (checklist B).
- **FE-008**: O número MUST ser alocado só depois da validação e imediatamente antes da assinatura (`08-emission-flows.md`). Uma falha após a reserva deixa lacuna auditada; a inutilização fica para a Fase 3.
- **FE-009**: `AUTHORIZED` MUST exigir protocolo `cStat` 100 com `chNFe` e `digVal` iguais aos do documento enviado. O `nfeProc` e o XML enviado MUST ser guardados imutáveis, com hash, versionados e com leitura fixada na versão (feature 004).
- **FE-010**: Diante de resultado incerto, o serviço MUST consultar pela chave antes de retransmitir. Retentativa técnica MUST NOT ser apresentada como contingência fiscal.
- **FE-011**: Estados, transições e enum MUST ser os de `FiscalDocument.status` do contrato (L10). O contrato MUST ganhar os endpoints de documento e exemplos de polling não terminal.
- **FE-012**: Toda emissão MUST gerar auditoria e telemetria redigidas. Chave de acesso, CPF e XML real nunca aparecem em log, trace ou repositório.

### Key Entities

FiscalDocument, FiscalDocumentItem, FiscalDocumentPayment, FiscalProtocol, SefazRequest e SefazResponse de `specs/001-fiscal-service/data-model.md`. Também as entidades da fundação: série, reserva, certificado, perfil e schema.

## Success Criteria

- **SE-001**: Todos os cenários do MVP da matriz E4 terminam no resultado esperado na homologação SEFAZ-SP.
- **SE-002**: Repetições idempotentes do mesmo payload devolvem o mesmo documento, sem nova numeração (SC-002).
- **SE-003**: Nenhuma retentativa após resultado incerto transmite antes de consultar (SC-005).
- **SE-004**: 100% dos autorizados têm XML enviado, `nfeProc`, protocolo, hash verificado e QR Code recuperáveis (SC-004).
- **SE-005**: 95% das solicitações são aceitas ou recusadas localmente em até 500 ms, sem contar o tempo da SEFAZ (SC-006).

## Gates de entrada (seção B do checklist, em aberto)

| Item | Situação | Bloqueia |
| --- | --- | --- |
| Perfil fiscal ratificado pelo contador e aprovado pela credencial aprovadora (T011) | resposta prevista até 2026-11-13 | qualquer emissão |
| Golden XML regenerado e fixado contra 010g v1.03, sem exclusivos do 010g | aberto | E4 |
| Sonda do 010g repetida depois de 2026-11-16 | aberto | liberar exclusivos do 010g |
| Seção do MOC 7.0 com os algoritmos de assinatura registrada (L8) | aberto | FE-003 |
| Rótulos de `tPag` extraídos do MOC congelado (L5) | aberto | E5 |
| Arredondamento pela regra do leiaute | aberto | FE-004 |
| Raízes ICP-Brasil fixadas por autorizador para TLS | aberto | FE-007 |
| Threat model revisado para o ambiente de deploy escolhido | provedor a definir; homologação local primeiro (decisão 2) | migração para a nuvem |
| `danfe-selftest` no container Linux (L3) | aberto | E5 |

## Out of Scope

Produção; cancelamento e inutilização; webhooks; contingência oficial; painel; NF-e 55; cenários sem decisão fiscal (8, 12, 18–22 e 31–40).

## Decisões do operador (2026-11-12)

1. **Modo de emissão: os dois.**
   - Síncrono: `indSinc=1`, como na POC. A resposta HTTP espera o resultado até o tempo-limite configurado. Se esse tempo acabar, o documento segue no assíncrono e a resposta é não terminal.
   - Assíncrono: `202` com o recurso do documento, e o cliente acompanha por polling.

   Os dois caminhos compartilham o mesmo estado, a mesma idempotência e a mesma consulta antes de reenvio.
2. **Homologação local primeiro**, com a stack endurecida da 004. A migração para a nuvem vem depois, quando o operador preparar a conta.

   Até lá, o item "threat model revisado para o ambiente de deploy" continua aberto e não bloqueia a homologação local.
3. **DANFE sem impressora física.** Quem imprime são os ERPs integrados. O serviço entrega:
   - o DANFE NFC-e pronto para impressão: HTML da POC, nas larguras de 80 e 58 mm;
   - os dados de impressão: URL do QR Code, conteúdo do QR e URL de consulta.

   A impressão é simulada por teste: o layout é renderizado em 80 e 58 mm, as margens e a zona de silêncio são conferidas, e o QR é decodificado da imagem renderizada e comparado com o esperado. A impressão térmica real (L4) continua no checklist C, para o ERP ou o operador fazer antes da produção.

## Assumptions

- Homologação SEFAZ-SP apenas, com o emitente real do operador. As etapas com o A1 real são executadas pelo operador, como na validação TM-03.
- O perfil `SP-SN-OWN-PRODUCTION-V1` é o único perfil do MVP, depois da ratificação do contador.
