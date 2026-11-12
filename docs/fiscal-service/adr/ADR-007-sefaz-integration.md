# ADR-007 — Integração com SEFAZ

**Status:** Proposto; detalhado em 2026-11-12 para a feature 005, aguarda aceite do operador
**Escopo:** autorização e consulta NFC-e na SEFAZ-SP (005, FE-007, FE-009, FE-010)

## Decisão original

Adaptadores por modelo, autorizador e UF, resolvidos por catálogo versionado de endpoints e capacidades. O domínio fala interfaces normalizadas; SOAP, envelopes, códigos e certificados ficam no adaptador.

## Detalhamento (2026-11-12)

1. **Projeto `Rrf5.Fiscal.Adapters.Sefaz`:**
   - interface `ISefazNfceClient` com `AuthorizeAsync` (`NFeAutorizacao4`, `indSinc=1`) e `ConsultAsync` (`NFeConsultaProtocolo4`, por chave);
   - devolve resultados normalizados: o domínio não vê SOAP.
2. **Endpoints:** saem só do catálogo (`sp-nfce-services`), por autorizador e ambiente, nunca do código. Um ambiente sem endpoint catalogado falha fechado.
3. **Transporte:**
   - SOAP 1.2 sobre TLS mútuo;
   - certificado de cliente: o A1 aberto pelo worker, com a cadeia do PFX montada offline (aprendizado da POC);
   - confiança no servidor: só a raiz ICP-Brasil fixada do autorizador (`Authorizer.TlsRootSha256` → `SefazTlsTrust`, 005 T005; SP: AC Raiz Brasileira v10);
   - validação TLS sempre ligada.
4. **Leitura das respostas:**
   - `retEnviNFe` e `protNFe`: `AUTHORIZED` só com `cStat` 100 **e** `chNFe` e `digVal` iguais aos do documento enviado (regra da POC, T018);
   - um protocolo 100 de outro documento é tratado como resultado incerto, não como autorização;
   - rejeições mantêm `cStat` e `xMotivo` oficiais, separados de falha técnica (FR-021).
5. **Tempo-limite e retentativa:**
   - tempo-limite de conexão e de resposta configurável;
   - retentativa técnica só **antes** de o pedido sair (falha de conexão ou de handshake);
   - depois do envio, qualquer dúvida vira consulta por chave (ADR-005, item 6).
6. **Rastro:**
   - `SefazRequest` e `SefazResponse` guardam hash, tamanho, tempos e a referência do objeto imutável no armazenamento;
   - o XML não vai para o banco nem para logs; o trace é sanitizado (sem chave de acesso, CNPJ ou CPF).
7. **Testes:** um servidor SOAP de teste (reescrita do mock da POC) cobre 104/100, 225, 217, timeout depois de autorizar e resposta ilegível. A homologação real roda por script do operador.

## Consequências

- **Positivas:** SP primeiro, outros autorizadores entram pelo catálogo e por adaptador, sem condicionais espalhadas.
- **Riscos:**
  - catálogo errado é risco crítico: mudanças de endpoint ou de raiz exigem fonte oficial, homologação e dupla aprovação;
  - se a cadeia TLS do autorizador mudar de raiz, a transmissão falha fechada até a nova raiz ser catalogada.
