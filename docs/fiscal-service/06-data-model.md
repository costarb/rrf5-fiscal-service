# Modelo de dados inicial

## Convenções

IDs UUID/ULID; timestamps com fuso; dinheiro em decimal; `tenant_id` obrigatório em dados de cliente; índices e uniques incluem ambiente/escopo. Segredos ficam em cofre ou envelope cifrado. XML/PDF ficam no object storage e a tabela guarda URI, hash, versão e retenção.

| Entidade                | Campos e chaves principais                                                                      | Índices/restrições                                      | Sensibilidade/retenção                 |
| ----------------------- | ----------------------------------------------------------------------------------------------- | ------------------------------------------------------- | -------------------------------------- |
| Tenant                  | id, name, status, quota                                                                         | name; status                                            | comercial; contrato + auditoria        |
| Application             | id, tenant_id, client_id, secret_hash, scopes                                                   | unique client_id                                        | credencial; rotação e revogação        |
| User                    | id, tenant_id, identity, status                                                                 | tenant+identity                                         | pessoal; minimização                   |
| Company                 | id, tenant_id, legal_name, tax_id, tax_regime                                                   | unique tenant+tax_id                                    | cadastral fiscal                       |
| Establishment           | id, company_id, tax_id, state_registration, address, UF                                         | company+tax_id                                          | fiscal/pessoal em endereço             |
| FiscalEnvironment       | id, establishment_id, type, status                                                              | unique establishment+type                               | separação rígida                       |
| DigitalCertificate      | id, owner, fingerprint, valid_from/to, vault_ref, status                                        | fingerprint; expiry                                     | crítico; sem exportação                |
| FiscalConfiguration     | id, environment_id, CSC ref, timezone, version                                                  | unique active per environment                           | CSC secreto                            |
| FiscalSeries            | id, environment_id, model, series, next_number, version                                         | unique env+model+series                                 | retenção fiscal                        |
| NumberReservation       | id, series_id, number, document_id, state                                                       | unique series+number                                    | auditoria permanente                   |
| TaxProfile              | id, tenant_id, code, status                                                                     | unique tenant+code                                      | decisão fiscal                         |
| TaxRule                 | id, profile_id, dimensions                                                                      | profile+dimensions                                      | decisão fiscal                         |
| TaxRuleVersion          | id, rule_id, valid_from/to, payload, approved_by                                                | intervalo sem sobreposição                              | histórico imutável                     |
| SchemaVersion           | id, model, version, valid_from/to, hash, storage_uri                                            | unique model+version+hash                               | artefato regulatório                   |
| FiscalDocument          | id, tenant_id, establishment_id, env, model, external_id, key, number, series, state, snapshots | unique tenant+establishment+external_id; unique env+key | fiscal; guarda legal                   |
| FiscalDocumentItem      | id, document_id, line, product_ref, values, tax_snapshot                                        | unique document+line                                    | fiscal                                 |
| FiscalDocumentPayment   | id, document_id, method, amount, acquirer data tokenized/masked                                 | document                                                | financeiro/fiscal                      |
| FiscalDocumentReference | id, document_id, type, referenced_key                                                           | document+key                                            | fiscal                                 |
| FiscalProtocol          | id, document/event, authority, code, number, received_at, artifact_hash                         | unique protocol context                                 | imutável                               |
| FiscalEvent             | id, document/series, type, sequence, state, payload_hash                                        | unique resource+type+sequence                           | imutável                               |
| ContingencyRecord       | id, document, mode, reason, started_at, deadline, reconciliation                                | status+deadline                                         | fiscal/operacional                     |
| SefazRequest/Response   | id, correlation, operation, endpoint_version, hashes, status, duration                          | correlation; created_at                                 | payload protegido; retenção controlada |
| IdempotencyRecord       | tenant, establishment, operation, key, request_hash, resource_id, response                      | unique tuple                                            | mínimo necessário                      |
| WebhookSubscription     | id, tenant, url, secret_ref, events, status                                                     | tenant+status                                           | secreto                                |
| WebhookDelivery         | id, subscription, event, attempt, status, next_at                                               | unique subscription+event+attempt                       | técnico                                |
| AuditLog                | id, tenant, actor, action, target, reason, result, occurred_at, metadata                        | tenant+time+target                                      | append-only                            |

## Integridade e isolamento

Foreign keys compostas ou validações transacionais garantem tenant consistente. Row-level security pode ser defesa adicional, nunca substituto do filtro aplicativo. Storage usa prefixos/chaves não previsíveis e autorização de download curta. Hashes detectam corrupção e o backup preserva versões.
