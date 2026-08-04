# Segurança, certificados e LGPD

## API

- OAuth 2.0 Client Credentials e JWT curto; secrets armazenados por hash e rotacionáveis.
- Scopes: `fiscal.documents.create/read/cancel/correct/download`, `fiscal.companies.manage`, `fiscal.certificates.manage`, `fiscal.settings.manage`, `fiscal.webhooks.manage`.
- RBAC adicional no painel; vínculo explícito aplicação-estabelecimento.
- rate limit por cliente/tenant/rota, allowlist e mTLS opcionais.
- correlation ID, limite de payload, schema estrito e replay protection.
- downloads por autorização ativa ou URL curta assinada; nunca bucket público.

## Certificado A1

Upload ocorre em canal autenticado, com verificação de formato, cadeia, validade, fingerprint e titularidade. Conteúdo e senha entram em envelope encryption: chave de dados por certificado, protegida por KMS/Vault. A senha não é armazenada em texto puro. Descriptografia ocorre apenas na memória do worker autorizado, pelo menor tempo possível, com buffers descartados e logs mascarados.

Interface de cofre deve permitir AWS, Azure, GCP, Vault ou solução local segura. A API não permite download/exportação do A1. Rotação, revogação, backup cifrado e acesso de emergência são auditados. Alertas: 60/30/15/7/1 dias e vencimento.

## Webhooks

HMAC-SHA256 sobre `timestamp.eventId.bodyHash`, secret por assinatura, tolerância temporal, nonce/eventId contra replay, rotação com sobreposição e cabeçalhos de versão. Retentativa exponencial com jitter, DLQ, histórico e reenvio manual. Consumidor deve deduplicar por `eventId` e consultar o recurso.

## LGPD e retenção

Dados pessoais possíveis: CPF, nome, endereço, telefone/e-mail e dados do responsável. Bases e prazos devem ser definidos pelo controlador com jurídico. Minimizar consumidor quando não obrigatório. Separar:

- comercial: pode admitir anonimização/descarte contratual;
- fiscal autorizado: imutável e sujeito à guarda legal;
- técnico: retenção curta, sem XML completo indiscriminado;
- auditoria: append-only e acesso restrito.

Solicitação de titular não apaga obrigação fiscal; responde com bloqueio/limitação legal e elimina dados auxiliares quando permitido.

## Ameaças prioritárias

Cross-tenant, exfiltração A1/CSC, SSRF em webhook, replay, payload XML malicioso, XXE, assinatura wrapping, downgrade de schema, troca de ambiente, brute force de download e operador interno. Testes e controles específicos são obrigatórios.
