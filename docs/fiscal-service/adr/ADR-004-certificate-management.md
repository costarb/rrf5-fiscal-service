# ADR-004 — Gerenciamento de certificado

**Status:** Aceito com evidência da fundação; sign-off da 002 aprovado em 2026-11-11  

## Decisão

A1 cifrado por envelope encryption atrás de `CertificateVault`; KMS/Vault é substituível. Senha nunca em claro, descriptografia apenas em worker autorizado e sem endpoint de exportação.

## Consequências

Eleva segurança e portabilidade, mas exige integração de cofre, rotação, recuperação e observabilidade de validade. A3/PAA serão novos provedores da interface, não mudanças no contrato público.

## Implementação na fundação (2026-11-11)

A1 e CSC só no cofre (OpenBao, envelope com chave de dados por segredo), sem endpoint de exportação; titular pelo OID 2.16.76.1.3.3 com CNPJ-Base igual ao do estabelecimento (MOC F03/213); alertas a 60/30/15/7/1 dias. Pendente antes da produção: validação da cadeia ICP-Brasil e rotação com controle duplo (threat model TM-03). Evidência: `CertificateInspectionTests`, `CertificateApiTests`, `CertificateExpiryTests`, `SecretCanaryTests`.
