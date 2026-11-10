# ADR-014 — Acesso a dados e migrações

**Status:** Proposto  
**Data:** 2026-11-10  
**Escopo:** PostgreSQL da fundação (FF-002, FF-005)

## Contexto

O `data-model.md` da 001 coloca o isolamento no banco: chaves compostas por
tenant, ambiente e série, RLS, triggers de imutabilidade e alocação de número
com `SELECT ... FOR UPDATE`. Esses mecanismos precisam ficar visíveis e
revisáveis, não gerados por uma ferramenta.

## Decisão

1. **Npgsql com SQL explícito**, sem ORM. Repositórios pequenos por agregado,
   sempre recebendo um contexto de tenant não nulo.
2. **Migrações em SQL versionado** em `db/migrations/NNNN_descricao.sql`,
   aplicadas por um migrador próprio em `Rrf5.Fiscal.Infrastructure` que grava
   número e SHA-256 de cada arquivo em `schema_migrations`. Um arquivo já
   aplicado cujo hash mudou impede a aplicação (nunca editar migração aplicada).
3. **Papéis separados**: `rrf5_owner` (DDL e migrações), `rrf5_app` (API, sem
   DDL e sem `BYPASSRLS`) e `rrf5_worker` (jobs). As tabelas com `tenant_id` usam
   `FORCE ROW LEVEL SECURITY`.
4. Contexto de tenant por transação (`set_config('app.tenant_id', ..., true)`),
   limpo antes de devolver a conexão ao pool.
5. Testes de banco rodam contra o PostgreSQL da stack local (mesma versão do
   Compose), nunca contra um banco em memória.

## Alternativas

- **EF Core**: produtividade maior, mas esconde SQL de restrições, RLS e locks
  que são o próprio requisito; exigiria SQL bruto nas partes críticas de qualquer
  modo.
- **DbUp ou Flyway para migrações**: maduros, porém mais uma dependência para
  um migrador de poucas dezenas de linhas com verificação de hash.
- **Dapper**: aceitável no futuro se o mapeamento manual crescer; passa pelo
  gate de dependências.

## Consequências

- Mais código de mapeamento manual; compensado por SQL auditável.
- Toda tabela nova precisa de teste de restrição e de RLS (lista do
  `data-model.md`).
- A dependência de runtime de dados é só o Npgsql (licença PostgreSQL).
