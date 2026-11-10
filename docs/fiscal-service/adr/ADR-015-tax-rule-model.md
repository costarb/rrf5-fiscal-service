# ADR-015 — Modelo de regra fiscal

**Status:** Proposto  
**Data:** 2026-11-10  
**Escopo:** TaxProfile, TaxRule e TaxRuleVersion da fundação (FF-007, FF-008); detalha o ADR-008

## Contexto

O ADR-008 define perfis e regras declarativas versionadas, sem motor genérico, e
o serviço valida consistência sem decidir classificação fiscal. O operador
decidiu em 2026-11-09 que o cadastro e a aprovação entram na fundação, com papel
de aprovador fiscal separado do autor.

## Decisão

1. **TaxProfile**: código de negócio estável por tenant (por exemplo,
   `SP-SN-OWN-PRODUCTION`), referenciado pelos itens (`fiscalProfileId`).
2. **TaxRule**: pertence ao perfil e é identificada por dimensões explícitas:
   modelo (`65`), UF (`SP`), operação (código de negócio, por exemplo
   `VENDA_CONSUMIDOR`) e classe de item (código de negócio do cliente).
3. **TaxRuleVersion**: payload imutável com o que o gerador monta (CRT, CFOP,
   grupo de ICMS e seus campos, PIS, COFINS, envio de IBS/CBS e de `vTotTrib`),
   vigência `[validFrom, validTo)`, referências de fonte obrigatórias, autor,
   aprovador e versão do gerador para a qual o payload foi validado.
4. **Estados**: `DRAFT → PENDING_APPROVAL → APPROVED → RETIRED`, e
   `PENDING_APPROVAL → DRAFT` em caso de recusa. Só `DRAFT` é editável. Aprovar
   exige o scope `fiscal.rules.approve` e principal diferente do autor; toda
   transição vai para a auditoria.
5. **Vigência sem sobreposição**: restrição de exclusão no PostgreSQL
   (`btree_gist`) sobre a regra e o intervalo, só para versões `APPROVED`.
   Vigência que começa antes da aprovação é recusada (sem efeito retroativo).
6. **Validação sem dependência nova**: desserialização estrita com
   `System.Text.Json` (membros desconhecidos recusados) para records tipados por
   grupo do leiaute suportado, mais regras de consistência no domínio. O JSON
   Schema publicado no contrato é gerado desses tipos. Grupo não suportado é
   recusado com o motivo.
7. **Resolução**: dado estabelecimento, dimensões e data, devolve a única versão
   `APPROVED` vigente, ou erro de domínio. O documento da Fase 2 guarda cópia da
   versão usada (FR-016).

## Consequências

- Acrescentar um grupo do leiaute (por exemplo, ICMS-ST) exige código e teste;
  trocar CST, CFOP ou alíquota de um grupo já suportado é só uma nova versão.
- O perfil da POC entra como `DRAFT` e só é aprovado depois da ratificação do
  contador (T011 da 001).
- O serviço continua sem escolher tributação: ele recusa o que não sabe montar
  ou o que é inconsistente, nunca preenche valor ausente.
