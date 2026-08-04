# ADR-004 — Gerenciamento de certificado

**Status:** Proposto

## Decisão

A1 cifrado por envelope encryption atrás de `CertificateVault`; KMS/Vault é substituível. Senha nunca em claro, descriptografia apenas em worker autorizado e sem endpoint de exportação.

## Consequências

Eleva segurança e portabilidade, mas exige integração de cofre, rotação, recuperação e observabilidade de validade. A3/PAA serão novos provedores da interface, não mudanças no contrato público.
