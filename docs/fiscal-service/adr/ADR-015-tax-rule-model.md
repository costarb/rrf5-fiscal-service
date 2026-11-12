# ADR-015 — Modelo de regra fiscal

**Status:** Aceito pelo operador em 2026-11-10; itens 1 a 3 alterados pelo ADR-017 (aceito em 2026-11-12)  
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

## Implementação (2026-11-11, T027–T032)

- **Payload do gerador `nfce-4.00-g1`:** `crt`, `cfop`, `icms`, `pis`, `cofins`, `ibsCbs` e `vTotTrib`.
  - `icms`, `pis` e `cofins` são discriminados por `group`, com o nome do elemento do leiaute.
  - Grupos suportados: `ICMSSN102`, `PISOutr` e `COFINSOutr` na opção ad valorem, os mesmos do perfil da POC.
  - Qualquer outro grupo, e qualquer pedido de envio de IBS/CBS ou de `vTotTrib`, é recusado com o código `rules.unsupported_group` e o nome do grupo.
- **Checagens de forma, todas tiradas de fonte oficial:**
  - CRT de 1 a 4, `orig` de 0 a 8, CSOSN do `ICMSSN102` e CST do `Outr` pelas enumerações do PL_010g;
  - CFOP pelo padrão `[123567]NNN`;
  - alíquota no formato `TDec_0302a04`;
  - CSOSN proibido com CRT 2 ou 3 (MOC N12a-20, rejeição 591). O CRT 4 (MEI) não é barrado, porque essa regra do MOC não trata dele.
- **Esquema publicado:** o JSON Schema sai dos próprios tipos em `GET /v1/tax-rule-payload.schema.json`.
- **Resolução:** usa a UF do estabelecimento, as dimensões e a data. O CRT da versão precisa ser igual ao CRT declarado do estabelecimento; se for diferente, a resposta é `rules.crt_mismatch` e o serviço não escolhe nada.
- **Fim de vigência:** uma versão aprovada sem data final pode receber `validTo` uma única vez, sempre de hoje em diante (`/close`), para dar lugar à sucessora.
- **Importação da discovery (`/v1/tax-profiles/import`):**
  - só aceita o status `INTERIM_OPERATOR_APPROVED` e sempre cria `DRAFT`;
  - as dimensões ficam modelo 65, UF SP, operação `VENDA` (o `natOp`) e classe `ncm-<NCM>`;
  - as fontes registradas são a decisão do operador e a ratificação pendente do contador (T011 da 001);
  - a aprovação acontece depois, pelo fluxo normal.
