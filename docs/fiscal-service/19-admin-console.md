# Painel administrativo e de suporte

## Áreas

- empresas/estabelecimentos e situação de onboarding;
- ambientes, CSC, séries, próximo número e reconciliação;
- certificados, fingerprint, titularidade, validade e rotação;
- perfis/regras fiscais, versões, vigências e aprovações;
- documentos, XML/DANFE, protocolos, eventos e rejeições;
- contingências, prazos, pendências e divergências;
- aplicações/scopes, webhooks, tentativas, DLQ e reenvio;
- status de autorizadores, schemas/NTs ativas e métricas;
- auditoria e exportação controlada de evidências.

## Perfis

- **Fiscal configurador**: perfis/regras e vigências, sem acesso a chave privada.
- **Operador**: consulta, contingência e reprocessamento técnico elegível.
- **Suporte**: diagnóstico sanitizado e webhook, sem PII/segredo por padrão.
- **Administrador de segurança**: aplicação, scopes, certificado e rotação.
- **Auditor**: somente leitura e trilhas.

Ações críticas — produção, certificado, CSC, série/número, regra, cancelamento, inutilização, contingência e reenvio manual — exigem scope/permissão, confirmação contextual, justificativa e auditoria append-only. Mudança de ambiente nunca é um simples seletor visual reutilizando credenciais.
