# Especificação da API

Base: `/api/v1`; HTTPS; JSON UTF-8; OAuth 2.0 Client Credentials; `Authorization`, `Idempotency-Key` em mutações e `X-Correlation-Id` recomendado.

## Recursos principais

| Método/rota                          | Escopo              | Resultado                                  |
| ------------------------------------ | ------------------- | ------------------------------------------ |
| POST `/documents/prevalidate`        | documents.create    | valida sem emitir/reservar definitivamente |
| POST `/documents`                    | documents.create    | emissão síncrona limitada ou 202           |
| POST `/documents/async`              | documents.create    | aceite assíncrono                          |
| GET `/documents/{id}`                | documents.read      | estado atual                               |
| GET `/documents?externalId=`         | documents.read      | busca idempotente do integrador            |
| GET `/documents/by-access-key/{key}` | documents.read      | consulta por chave autorizada              |
| GET `/documents/{id}/xml`            | documents.download  | XML permitido                              |
| GET `/documents/{id}/danfe`          | documents.download  | PDF/representação                          |
| GET `/documents/{id}/events`         | documents.read      | timeline                                   |
| POST `/documents/{id}/cancel`        | documents.cancel    | evento com justificativa                   |
| POST `/number-voids`                 | documents.cancel    | inutilização de faixa                      |
| POST `/documents/{id}/reprocess`     | documents.correct   | apenas falha técnica elegível              |
| CRUD `/companies`, `/establishments` | companies.manage    | onboarding                                 |
| POST `/certificates`                 | certificates.manage | upload seguro A1                           |
| PUT `/settings/csc`                  | settings.manage     | CSC por ambiente                           |
| CRUD `/tax-profiles`                 | settings.manage     | regras versionadas                         |
| CRUD `/webhooks`                     | webhooks.manage     | inscrições e reenvio                       |
| GET `/authorizers/status`            | documents.read      | visão operacional                          |

## Resposta e erros

```json
{
  "id": "doc_01J...",
  "externalId": "PEDIDO-12345",
  "status": "AUTHORIZED",
  "documentType": "NFCE",
  "environment": "PRODUCTION",
  "accessKey": "3526...",
  "protocol": "13526...",
  "number": 123,
  "series": 1,
  "issuedAt": "2026-08-04T14:00:00-03:00",
  "xmlUrl": "/api/v1/documents/doc_01J.../xml",
  "danfeUrl": "/api/v1/documents/doc_01J.../danfe",
  "sefaz": { "code": "100", "message": "Autorizado o uso da NF-e" }
}
```

Envelope de erro: `error.code`, `message`, `correlationId`, `details[]`, `retryable`. Taxonomia: `VALIDATION_ERROR`, `FISCAL_CONFIGURATION_ERROR`, `CERTIFICATE_ERROR`, `SCHEMA_VALIDATION_ERROR`, `SEFAZ_REJECTION`, `SEFAZ_UNAVAILABLE`, `DUPLICATE_REQUEST`, `IDEMPOTENCY_CONFLICT`, `DOCUMENT_ALREADY_AUTHORIZED`, `CANCELLATION_DEADLINE_EXCEEDED`, `UNAUTHORIZED_ESTABLISHMENT`, `RESULT_UNKNOWN`, `INTERNAL_PROCESSING_ERROR`.

Rejeições fiscais usam HTTP coerente com a operação aceita (frequentemente 200/422 conforme fase) e código de domínio; nunca 500 por rejeição conhecida. `429` informa retry; `409` cobre conflito idempotente/estado; `202` inclui `Location` e `Retry-After`.

## Idempotência

Tuple: tenant + estabelecimento + operação + chave. Retém request hash e resposta/recurso. Mesmo hash reproduz resposta; hash diferente retorna `409 IDEMPOTENCY_CONFLICT`. Registro permanece no mínimo durante toda janela operacional e não expira enquanto houver documento não terminal ou obrigação de deduplicação associada.
