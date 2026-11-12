# ADR-017 — Natureza de operação e atributos do produto vindos do ERP

**Status:** Aceito pelo operador em 2026-11-12 (decisões 1 e 6 tomadas pelo operador no mesmo dia)
**Escopo:** pré-validação e emissão da 005 (FE-005, T010, T013); altera o ADR-015 nos itens 1, 2 e 3

## Contexto

- **O cadastro de produto vive no ERP.** No RRF5 OS, NCM, CFOP, CSOSN, origem, unidade, CEST e GTIN são características de cada produto (perfis fiscais do cadastro).
- **O ADR-015 e a T010 inverteram essa responsabilidade.** Neles, o item mandava `fiscalProfileId` e `itemClass`, e o CFOP, o CSOSN e a origem saíam da regra.
- **O modelo do Bling é diferente.** Lá, a natureza de operação reaplica o CFOP e o CSOSN por regra (padrão mais exceção por NCM) e ignora os valores do produto. As divergências aparecem como pendência só depois de a nota estar autorizada.
- **A tabela por produto foi validada.** O contador validou a tabela em 2026-11-12 (`pocs/evidence/fiscal-review.md`).

## Decisão

1. **O ERP envia, por item:** `ncm`, `cfop`, `csosn`, `origin`, `unit`, `gtin` e, quando houver, `cest`.
   - O serviço confere esses valores contra os padrões do leiaute 010g: NCM `[0-9]{2}|[0-9]{8}`, CEST `[0-9]{7}`, cEAN `SEM GTIN|[0-9]{8}|[0-9]{12,14}`, `orig` de 0 a 8 e `uCom` de 1 a 6 caracteres.
   - Um GTIN numérico também precisa ter o dígito verificador GS1 correto.
2. **Natureza de operação = `TaxProfile`.** O documento informa a natureza pelo código (`nature`) e a `operation` (dimensão da regra).
   - O item deixa de levar `fiscalProfileId` e `itemClass`.
   - As tabelas `tax_profile`, `tax_rule` e `tax_rule_version` e o fluxo de quatro olhos do ADR-015 não mudam.
3. **Seleção da regra dentro da natureza:**
   - primeiro a classe `ncm-<NCM>`, a exceção por NCM;
   - se ela não existir, a classe `ANY`, a regra padrão.
   - Precisa haver uma versão `APPROVED` vigente na data. Sem regra, o item é recusado e o serviço não escolhe nada.
4. **Conferência (opção B do operador):** o `cfop` e o `csosn` enviados precisam ser iguais aos da versão aprovada que cobre o item.
   - Se forem diferentes, o problema `document.tax_mismatch` aponta o campo, e o documento não é numerado nem enviado.
   - PIS, COFINS e o CRT continuam vindo da regra, porque não fazem parte do cadastro do produto.
5. **A origem sai do payload da regra.** O grupo `ICMSSN102` do payload passa a ter só o `csosn`, e a `orig` vem do item.
   - Nenhuma versão foi aprovada até hoje; só existe o DRAFT importado na 002, ambiente local. A versão do gerador continua `nfce-4.00-g1`.
6. **CSOSN 500 (revenda com ST) fica para um incremento posterior do gerador.** Ele precisa de `ICMSSN500`, CEST e ICMS-ST retido.
   - Até lá, uma regra com esse grupo é recusada com `rules.unsupported_group`, e o item sem regra aprovada é recusado na pré-validação.
7. **Evidência no documento:** cada item guarda os atributos recebidos, a classe escolhida e a versão de regra que os conferiu (FR-016).

## Consequências

- **O ERP é dono da classificação do produto.** A validação fiscal de NCM e CEST exigida pelo FR-017 acontece no cadastro do ERP, com o contador.
  - O serviço garante o formato e que só sai nota com CFOP e CSOSN cobertos por uma regra aprovada.
- **Divergências viram recusa antes do envio,** e não pendência depois da autorização (diferente do Bling).
  - Uma correção no cadastro do ERP que não tenha regra aprovada correspondente bloqueia a emissão daquele item até a regra ser aprovada.
- **O mesmo NCM com dois CFOP na mesma natureza não é suportado nesta versão** (por exemplo, um item de produção própria e de revenda). Se aparecer, a classe de item ganha o CFOP como dimensão em um ADR posterior.
- **A dimensão "destino" do Bling não se aplica.** A NFC-e gerada é sempre operação interna e presencial (`idDest` 1).
- **Contrato:** muda o item de `CreateDocument`/pré-validação. Não há ERP integrado, então não existe consumidor a migrar.
