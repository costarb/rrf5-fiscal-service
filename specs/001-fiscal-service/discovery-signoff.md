# Sign-off da discovery — RRF5 Fiscal Service (T037)

**Status:** APROVADO pelo operador em 2026-11-09  
**Preparado em:** 2026-11-09  
**Branch:** `feature/001-fiscal-discovery` (28 commits à frente de `develop`)  
**Escopo:** Fase 0, NFC-e modelo 65, SEFAZ-SP, ambiente de homologação

Este sign-off aprova a **conclusão técnica da discovery** e o merge em `develop`. Ele
não aprova tratamento tributário, nem emissão em produção, nem contingência. A
documentação oficial e a configuração fiscal aprovada prevalecem.

## 1. Evidências revisadas

| Área | Tarefas | Evidência | Resultado |
| --- | --- | --- | --- |
| Fontes normativas e baseline | T001–T010 | `pocs/evidence/normative-baseline.md`, `regulatory/catalog.json` | Pacote vigente 010g v1.03 congelado com hash do ZIP oficial (2026-11-08) |
| Perfil fiscal | T011 | `pocs/evidence/fiscal-review.md`, `fiscal-profile.md` | **Pendente (externo):** só aprovação provisória do operador, válida para POC e homologação |
| XML, XSD e XMLDSig | T012–T017 | `xml-xsd-poc.md`, `xmldsig-poc.md` | PASS nos três candidatos; golden byte-idêntico |
| SOAP e homologação SEFAZ-SP | T018–T019 | `homologation.md` | PASS no .NET: cStat 100 e consulta |
| Resultado incerto | T020 | `homologation.md`, `pocs/shared/uncertain-result` | PASS: consulta antes de reenviar |
| DANFE e QR v3 | T021–T022 | `danfe-qr.md` | PASS (online); impressão térmica pendente (L4) |
| Container, desempenho, segredos | T023–T026 | `container-poc.md`, `performance-bench.md`, `secret-handling.md`, `threat-model.md` | PASS no escopo de POC |
| Contrato e integração BurgoOS | T027–T028 | `contracts/openapi.yaml`, `burgoos-contract-review.md` | Válido; lacunas registradas (L10) |
| Escolha de stack e biblioteca | T029–T032 | `runtime-library-scorecard.md`, ADR-011, ADR-012, `dependency-review.md` | Aceitas: .NET 10 LTS, implementação própria |
| Limitações e bloqueios | T033 | `discovery-results.md` | L1–L12 e E1–E6 registrados |
| Checklist da fundação | T034 | `checklists/foundation-entry.md` | Criado |
| Validação completa | T035 | `validation-run.md` | PASS, inclusive o CI (run 42544967193) |
| Auditoria do repositório | T036 | `repository-audit.md` | PASS: nenhum certificado, CSC, credencial ou XML com dados pessoais versionado |

## 2. Pendências que não bloqueiam o merge

Ficam abertas com responsável. Nenhuma delas é marcada como resolvida por este sign-off:

| Item | Responsável | Bloqueia |
| --- | --- | --- |
| E1 / T011: ratificação do perfil fiscal pelo contador, incluindo IBS/CBS para CRT 1 em 2026 e CST 49 de PIS/COFINS | Contador (semana de 2026-11-09) | Produção |
| E2: fonte de `vTotTrib` | Operador + contador | Produção (DANFE) |
| E3: revisão jurídica de redistribuição | Jurídico/comercial | Distribuição |
| E4: pacote de schema validado pela SEFAZ-SP (`verAplic` `SP_NFCE_PL_009_V400` contra a baseline 010g v1.03) | Operador | **Abrir a branch da fundação** (checklist A) |
| E6: contingência offline | Responsável fiscal | Fica fora do MVP |
| L1–L12 | Fundação | Checklist B/C |

T011 é a única tarefa da Fase 0 que fica aberta. Ela é externa e não altera a
escolha de stack nem de biblioteca.

## 3. O que este sign-off autoriza

- Merge de `feature/001-fiscal-discovery` em `develop` por pull request, com o CI verde.
- Nada além disso. A `feature/002-fiscal-foundation` só é aberta depois que o
  checklist A estiver completo, o que inclui E4. Emissão em produção continua
  bloqueada por E1, E2 e pelo checklist C.

## 4. Decisão

- [x] Aprovo a conclusão da discovery e o merge em `develop`, com as pendências da seção 2
- [ ] Não aprovo. Motivo:

- Aprovador (papel; identidade fora do Git): operador e proprietário do emitente
- Data: 2026-11-09
- Commit revisado: `cb2a8de` (evidências até a T036; este registro e a correção de E4 vêm no commit seguinte)

## Adendo — 2026-11-09 (E4)

Depois do sign-off, o operador fechou a E4 por decisão própria, sem confirmação
oficial do pacote que a SEFAZ-SP aplica. Base: cronograma oficial das NT
2026.006 e 2026.008 v1.01 e a rodada 5 de `homologation.md`, em que a
homologação SP respondeu 225 a um documento com os campos opcionais do 010g.
Regra: na SP, os documentos levam só conteúdo comum a 010e, 010f e 010g; a sonda
do 010g é repetida depois de 2026-11-16 antes de habilitar esses campos
(checklist B). Com isso o checklist A está completo.
