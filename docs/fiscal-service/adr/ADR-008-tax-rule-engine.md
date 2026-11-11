# ADR-008 — Motor de regras fiscais

**Status:** Aceito com evidência da fundação (2026-11-11), sujeito ao sign-off da 002  

## Decisão

Usar perfis fiscais e regras declarativas versionadas por dimensões e vigência. Não criar motor genérico/Turing-complete no MVP. Toda versão tem autoria e aprovação fiscal; documento guarda snapshot.

## Consequências

Reduz complexidade e facilita auditoria. Casos não expressáveis exigem evolução controlada. O sistema valida consistência, não decide classificação fiscal autonomamente.

## Implementação na fundação (2026-11-11)

Implementado conforme ADR-015: perfil, regra com dimensões e versão imutável, quatro olhos no app e no banco, vigência sem sobreposição e sem retroatividade, payload estrito só com grupos que o gerador monta. Evidência: `RulePayloadTests`, `TaxRuleConstraintTests`, `TaxRuleLifecycleTests`.
