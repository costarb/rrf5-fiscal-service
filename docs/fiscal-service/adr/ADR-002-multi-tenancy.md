# ADR-002 — Estratégia multi-tenant

**Status:** Aceito com evidência da fundação (2026-11-11), sujeito ao sign-off da 002  

## Decisão

Banco compartilhado com `tenant_id` obrigatório e chaves/índices compostos no MVP, autorização por aplicação-estabelecimento e testes cruzados. RLS pode ser defesa adicional. Tenants regulados/maiores poderão receber banco/storage dedicados.

## Consequências

Custo inicial menor e operação simples, mas toda consulta exige escopo verificável. Certificado, CSC, série e documento nunca são selecionados apenas por ID global sem tenant/estabelecimento.

## Implementação na fundação (2026-11-11)

Isolamento por tenant em três camadas: chaves compostas (0002–0006), RLS forçado com contexto por transação (0007) e filtro explícito de tenant em todo repositório; recurso de outro tenant responde 404. Evidência: `RowLevelSecurityTests`, `ConstraintTests`, `CrossTenantTests`, `IssuerIsolationTests`, `TwoTenantOnboardingTests`.
