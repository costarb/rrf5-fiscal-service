# ADR-008 — Motor de regras fiscais

**Status:** Proposto

## Decisão

Usar perfis fiscais e regras declarativas versionadas por dimensões e vigência. Não criar motor genérico/Turing-complete no MVP. Toda versão tem autoria e aprovação fiscal; documento guarda snapshot.

## Consequências

Reduz complexidade e facilita auditoria. Casos não expressáveis exigem evolução controlada. O sistema valida consistência, não decide classificação fiscal autonomamente.
