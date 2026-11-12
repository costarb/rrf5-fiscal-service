# ADR-014 — Acesso a dados e migrações

**Status:** Aceito pelo operador em 2026-11-10  
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

## Ajustes ao pseudocódigo do `data-model.md` (2026-11-10, T007)

As migrações `db/migrations/0001` a `0007` seguem o pseudocódigo normativo da
001 e mantêm todas as chaves compostas e checks. Elas diferem em quatro pontos:

1. **`fiscal_series.next_number`** aceita até `number_max + 1`, que marca a faixa
   esgotada. Com o teto em `number_max`, o último número da faixa nunca podia
   ser alocado. Coberto pelo teste `LastNumberOfTheRangeIsAllocatableAndThenTheSeriesIsExhausted`.
2. **`idempotency_record`** também serve às mutações administrativas:
   - `establishment_id` é opcional, com unicidade `NULLS NOT DISTINCT`;
   - a chave pertence ao tenant de quem chama;
   - a resposta pode ser repetida e nunca guarda segredo;
   - `resource_id` é genérico; o vínculo com documento fica em `document_id`, com chave composta.
3. **`digital_certificate`** fica preso a um ambiente (FF-004), com no máximo um
   certificado `ACTIVE` por ambiente. O mesmo A1 usado nos dois ambientes vira
   duas linhas.
4. **Códigos de erro próprios** (`RF001` a `RF006`, definidos em `0001_base.sql`)
   identificam as violações de imutabilidade, de monotonicidade, de transição,
   de quatro olhos, de append-only e de consistência. O T012 os mapeia para
   Problem Details.

Algumas regras foram para o banco como defesa em profundidade:

- o aprovador precisa ter `fiscal.rules.approve`;
- a vigência não pode começar antes da data da aprovação, no fuso de São Paulo;
- uma reserva só aceita o `owner_epoch` atual e um número que a série já entregou;
- a troca de dono da série exige incrementar o `owner_epoch`.
