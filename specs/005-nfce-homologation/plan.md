# Implementation Plan: Emissão NFC-e em homologação

**Branch**: `feature/005-nfce-homologation` | **Date**: 2026-11-12 | **Spec**: [spec.md](./spec.md)

## Summary

Levar a emissão NFC-e modelo 65 para o código de produto, sobre a fundação (002),
o controle duplo de certificado (003) e o cofre e bucket endurecidos (004). O fluxo:
- pré-validação;
- emissão síncrona e assíncrona;
- XML na ordem do leiaute e validação XSD do pacote resolvido;
- assinatura no worker;
- transmissão SOAP com TLS mútuo;
- interpretação do protocolo;
- consulta antes de reenvio;
- guarda imutável do `nfeProc`;
- QR Code e DANFE pronto para impressão pelo ERP.

Tudo roda localmente contra a homologação da SEFAZ-SP. A feature termina com a
matriz do food truck exercitada nessa homologação. Produção, cancelamento,
inutilização, webhooks e contingência ficam para a Fase 3.

## Decisões de entrada

| # | Decisão | Origem |
| --- | --- | --- |
| D1 | Emissão síncrona e assíncrona sobre o mesmo estado, idempotência e consulta | Operador, 2026-11-12 |
| D2 | Homologação local, na stack endurecida da 004; nuvem depois. AWS sa-east-1 recomendada, sem decisão | Operador, 2026-11-12 |
| D3 | DANFE entregue pronto para impressão em 80 e 58 mm, mais os dados de impressão; os ERPs imprimem; a impressão é simulada por teste | Operador, 2026-11-12 |
| D4 | Implementação fiscal própria; a POC é reescrita com testes, não copiada | ADR-011, plano da 002 |
| D5 | Na SP, só conteúdo comum a 010e–010g até a sonda do 010g ser aceita | E4, ADR-009 |
| D6 | Só o worker abre o A1 e o CSC (política `rrf5-worker`); a API não assina | ADR-004, feature 004 |
| D7 | Etapas com o A1 real ou o emitente real são executadas pelo operador, por script | Prática da 003 |
| D8 | O resultado incerto fica em `WAITING_SEFAZ`, que é o estado do enum fechado, e é reconciliado por consulta; nenhum estado novo | Contrato da 001, L10 |

## Decisões a registrar nesta feature

| ADR | Tema | Recomendação inicial (a confirmar no ADR) |
| --- | --- | --- |
| ADR-005 | Modelo de processamento | A API grava o documento e uma mensagem (outbox) na mesma transação; o worker consome via RabbitMQ, assina e transmite. O síncrono espera o resultado com tempo-limite e, se o tempo acabar, devolve `202` com o documento não terminal |
| ADR-007 | Integração SEFAZ | Adaptador por autorizador com endpoints do catálogo; SOAP 1.2 e TLS mútuo com o A1; raízes ICP-Brasil fixadas por autorizador; tempo-limite e retentativa técnica só antes do envio ou depois de consultar |
| ADR-016 | Geração, assinatura e QR/DANFE | Gerador XML próprio por versão de leiaute; XSD do pacote resolvido; XMLDSig conforme a seção do MOC registrada (L8); QR e DANFE pelo Manual DANFE NFC-e v6.0 |

ADR-010 (contingência) continua "Proposto". Esta feature não implementa contingência.

## Technical Context

**Novos projetos**:
- `Rrf5.Fiscal.Nfce`: modelo de documento, gerador XML, validação XSD, chave de acesso, QR Code e DANFE HTML. Não faz I/O, exceto ler o XSD do armazenamento de artefatos.
- `Rrf5.Fiscal.Adapters.Sefaz`: cliente SOAP, TLS mútuo e leitura de `retEnviNFe`, `protNFe` e `retConsSitNFe`.

**Fila**: RabbitMQ (já na stack) com outbox no PostgreSQL; entrega pelo menos uma vez, e o worker deduplica pelo estado do documento.

**Testes**:
- mock SOAP da POC (T018) reescrito como servidor de teste, com os cenários 104/100, 225, 217 e timeout;
- matriz do food truck como fixtures sintéticas;
- execução real na homologação por script do operador.

**Dependências**: só pacotes já aprovados, mais o que o gate de `dependency-review.md` aceitar. São candidatos um decodificador de QR para o teste de impressão simulada e um renderizador de HTML para imagem; os dois só entram em teste.

## Project Structure (adições)

```text
src/
|-- Rrf5.Fiscal.Nfce/              # documento, XML, XSD, chave, QR, DANFE
|-- Rrf5.Fiscal.Adapters.Sefaz/    # SOAP, mTLS, respostas
db/migrations/                     # itens, pagamentos, protocolo, outbox, requisições SEFAZ
tests/
|-- Nfce/                          # golden XML, XSD, assinatura, arredondamento, QR, DANFE
|-- Integration/                   # emissão ponta a ponta contra o mock SOAP
scripts/operator/                  # homologação real e matriz do food truck (A1 real)
```

## Governance Check

- **Material real fora do Git.** Nenhum certificado, CSC, chave de acesso real, CNPJ real ou XML real entra no Git. O XML enviado e o `nfeProc` reais ficam só no armazenamento local, e os quadros de resultado não levam chave.
- **Nenhuma tributação inferida.** O perfil aprovado decide; uma regra ausente bloqueia antes de numerar.
- **Perfil ratificado antes de emitir.** Só depois da ratificação do contador (T011) o perfil é aprovado pela credencial aprovadora, com `validFrom` a partir da aprovação. Antes disso, só o mock.
- **Exclusivos do 010g desligados** até a sonda de depois de 2026-11-16 ser aceita.
- **Retentativa técnica não é contingência**, nem na interface nem na documentação.

## Fases

1. **Gates normativos**, os itens da seção B que não dependem do contador:
   - golden XML regenerado no 010g;
   - seção de assinatura do MOC (L8);
   - tabela de `tPag` (L5);
   - regra de arredondamento;
   - raízes ICP-Brasil por autorizador para TLS.
2. **Modelo e persistência**: migrações de itens, pagamentos, protocolo, requisição e resposta SEFAZ e outbox; transições de estado no banco.
3. **E2 — Pré-validação**: cadastro, A1 ativo, CSC, perfil vigente, totais e pagamentos, sem numerar.
4. **Geração e assinatura**: chave de acesso, XML, XSD e XMLDSig no worker com o A1 do cofre.
5. **Adaptador SEFAZ**: SOAP com TLS mútuo, leitura das respostas, mock de teste.
6. **E1 e E3 — Emissão e resultado incerto**: síncrona e assíncrona, consulta antes de reenvio, guarda do `nfeProc`.
7. **E5 — QR e DANFE**: QR Code, DANFE HTML em 80 e 58 mm, dados de impressão, impressão simulada e `danfe-selftest` no container.
8. **E4 — Homologação real**: perfil aprovado, script do operador e matriz do food truck.
9. **Fechamento**: contrato e lint, threat model (TM-06, TM-08, TM-09 e TM-16), checklist B, quickstart e sign-off.

## Acceptance Gates

| Gate | Evidência |
| --- | --- |
| XML | golden XML válido no XSD do 010g v1.03, sem exclusivos do 010g; corpus negativo de XXE, expansão de entidades e IDs duplicados (TM-09) |
| Assinatura | algoritmo e transformações da seção do MOC registrada; assinatura validada antes do envio; a API não consegue assinar |
| Autorização | `AUTHORIZED` só com `cStat` 100 e `chNFe`/`digVal` iguais aos enviados (mock e homologação real) |
| Incerteza | timeout depois de autorizar na SEFAZ termina `AUTHORIZED` sem retransmitir; consulta indisponível mantém `WAITING_SEFAZ` com alerta |
| Idempotência | mesmo payload devolve o mesmo documento; payload diferente devolve conflito; nenhum número novo |
| Numeração | número só depois da validação; falha depois da reserva vira lacuna auditada |
| Impressão simulada | DANFE em 80 e 58 mm com margens e zona de silêncio conferidas; QR decodificado igual ao esperado |
| Homologação | matriz do food truck com o resultado esperado em todos os cenários do MVP |
| Segredos | canários de A1, CSC e CPF ausentes de logs, traces, respostas e repositório |

## Risks

- **Perfil fiscal sem ratificação:** sem ela não há emissão real. As fases 1 a 7 andam contra o mock.
- **Sonda do 010g:** se a SP continuar devolvendo 225 depois de 2026-11-16, os exclusivos seguem desligados e a matriz roda só com o conteúdo comum.
- **Homologação instável ou lenta:** os tempos-limite do síncrono são configuráveis, e a matriz registra latência e `cStat` por cenário sem tratar falha técnica como rejeição.
- **Escopo crescer para a Fase 3:** cancelamento, inutilização e contingência ficam fora desta branch.
