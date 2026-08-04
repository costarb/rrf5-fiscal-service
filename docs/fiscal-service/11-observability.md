# Observabilidade e operação

## Telemetria

Logs estruturados: correlation ID, document ID, tenant, estabelecimento, ambiente, modelo, operação, estado, código SEFAZ e duração. Não registrar senha, chave privada, token, secret, cartão completo ou XML indiscriminado.

Métricas:

- solicitações/autorizadas/rejeitadas/falhas/canceladas/contingência;
- latência total, interna e SEFAZ;
- fila, idade, retentativas e DLQ;
- webhooks pendentes/falhos;
- certificado por janela de vencimento;
- rejeição por código/modelo/UF/versão;
- volume por tenant/estabelecimento;
- disponibilidade/circuito por autorizador;
- lacunas, conflito de número e resultado incerto.

Traces atravessam API, job, assinatura, storage, SEFAZ e webhook, preservando amostragem total para falhas críticas.

## Alertas

- certificado ≤ 30 dias ou inválido;
- fila acima do SLO/sem consumidor;
- crescimento de `RESULT_UNKNOWN`, duplicidade ou divergência de numeração;
- rejeição anormal por versão/schema;
- storage/backup/hash inconsistente;
- webhook DLQ;
- isolamento/autorização negada anormal;
- autorizador indisponível, com separação de ambiente.

Dashboards: executivo por volume/SLO; operação fiscal por estado/rejeição/contingência; segurança por credencial/certificado; regulatório por versão e vigência. Status page comunica somente impacto sanitizado.
