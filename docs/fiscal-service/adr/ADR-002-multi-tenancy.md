# ADR-002 — Estratégia multi-tenant

**Status:** Proposto

## Decisão

Banco compartilhado com `tenant_id` obrigatório e chaves/índices compostos no MVP, autorização por aplicação-estabelecimento e testes cruzados. RLS pode ser defesa adicional. Tenants regulados/maiores poderão receber banco/storage dedicados.

## Consequências

Custo inicial menor e operação simples, mas toda consulta exige escopo verificável. Certificado, CSC, série e documento nunca são selecionados apenas por ID global sem tenant/estabelecimento.
