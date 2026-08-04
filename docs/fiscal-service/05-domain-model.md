# Modelo de domínio

## Agregados

- **Tenant** controla aplicações, quotas e isolamento.
- **Company** representa pessoa jurídica; **Establishment** representa unidade emissora.
- **FiscalConfiguration** agrega ambiente, CSC, séries e políticas aprovadas.
- **DigitalCertificate** mantém metadados e referência segura, nunca chave em objetos de API.
- **TaxProfile** possui **TaxRuleVersion** com vigência e aprovação.
- **FiscalSeries** é raiz de concorrência de numeração.
- **FiscalDocument** é agregado da emissão e referencia snapshots de regra/schema/certificado.
- **FiscalEvent** representa cancelamento, inutilização e demais eventos oficiais.
- **ContingencyRecord** governa decisão, transmissão posterior e reconciliação.

## Invariantes

1. Tenant, estabelecimento, certificado, série e regra pertencem ao mesmo escopo autorizado.
2. Ambiente do documento coincide com certificado/configuração/série/CSC/endpoints.
3. Número não é reutilizado; rollback produz lacuna tratável, nunca decremento inseguro.
4. Documento autorizado só muda por evento oficial; artefato original é imutável.
5. Idempotency key + hash incompatível resulta em conflito.
6. Uma versão fiscal só é aplicada dentro da vigência e preservada no snapshot.
7. Pagamentos, itens e totalizadores fecham antes da assinatura.

## Eventos de domínio

`document.created`, `document.validated`, `document.validation_failed`, `document.queued`, `document.sent`, `document.authorized`, `document.rejected`, `document.cancelled`, `document.contingency_created`, `document.contingency_transmitted`, `document.error`, `certificate.expiring`, `certificate.expired`, `sefaz.unavailable`, `sefaz.available`.

Eventos possuem `eventId`, `sequence`, `occurredAt`, `tenantId`, `establishmentId`, recurso, ambiente e payload sanitizado. Evento interno e evento fiscal oficial são tipos distintos.
