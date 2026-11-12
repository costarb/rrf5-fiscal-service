# ADR-004 — Gerenciamento de certificado

**Status:** Aceito com evidência da fundação; sign-off da 002 aprovado em 2026-11-11  

## Decisão

A1 cifrado por envelope encryption atrás de `CertificateVault`; KMS/Vault é substituível. Senha nunca em claro, descriptografia apenas em worker autorizado e sem endpoint de exportação.

## Consequências

Eleva segurança e portabilidade, mas exige integração de cofre, rotação, recuperação e observabilidade de validade. A3/PAA serão novos provedores da interface, não mudanças no contrato público.

## Implementação na fundação (2026-11-11)

A1 e CSC só no cofre (OpenBao, envelope com chave de dados por segredo), sem endpoint de exportação; titular pelo OID 2.16.76.1.3.3 com CNPJ-Base igual ao do estabelecimento (MOC F03/213); alertas a 60/30/15/7/1 dias. Pendente antes da produção: validação da cadeia ICP-Brasil e rotação com controle duplo (threat model TM-03). Evidência: `CertificateInspectionTests`, `CertificateApiTests`, `CertificateExpiryTests`, `SecretCanaryTests`.

## Atualização (2026-11-11, feature 003)

- **Cadeia ICP-Brasil:** passou a ser exigida, montada só contra as raízes do pacote vigente do ITI (catalogado com hash), com LCR consultada online e falha fechada.
- **Ativação com controle duplo:** o envio fica `PENDING_APPROVAL` e é ativado por outra aplicação com `certificates.approve`; os dois scopes nunca vão juntos. O banco também garante isso (migração 0010).
- **Revogação (decidido em 2026-11-12):** LCR consultada online, com falha fechada (`certificate.revocation_unavailable`) quando não responde. O padrão está em `Certificates:Revocation = Online`; mudar exige nova decisão registrada aqui.
- **Validação com o A1 real (2026-11-12):** executada pelo operador com `scripts/operator/validate-a1-dual-control.ps1` na stack local. As 6 verificações passaram: cadeia até a AC Raiz Brasileira v5, sem revogação na LCR, e os perfis gestor e aprovador segregados. Registro em `pocs/evidence/threat-model.md` (TM-03).
