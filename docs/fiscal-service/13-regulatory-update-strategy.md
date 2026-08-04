# Estratégia de atualização regulatória

## Processo

1. Monitorar Portal Nacional, CONFAZ e SEFAZs oficiais.
2. Registrar documento, versão, hash, publicação, cronograma e modelos/UFs.
3. Analisar impacto por schema, regra, API, DANFE, evento, storage e operação.
4. Importar artefatos em catálogo imutável e abrir versão de regra.
5. Criar fixtures de regressão e executar homologação.
6. Aprovar por engenharia + fiscal; comunicar integradores.
7. Ativar por ambiente/data com feature flag e relógio confiável.
8. Monitorar rejeições; rollback troca versão para novas emissões, nunca altera documentos emitidos.

## Reforma Tributária

| Tema              | Situação em 04/08/2026                          | Vigência                                 | Impacto                                  | Ação                                    |
| ----------------- | ----------------------------------------------- | ---------------------------------------- | ---------------------------------------- | --------------------------------------- |
| IBS/CBS           | NT 2025.002 v1.40 publicada em 20/05/2026       | conforme cronograma oficial              | novos grupos, totalizadores e validações | catálogo versionado + fixtures por data |
| Protocolo         | SP informou teste com 17 posições em 11/03/2026 | produção a divulgar na página consultada | tamanho/contrato não pode ser fixo       | armazenar como string compatível        |
| CNPJ alfanumérico | NT Conjunta 2025.001 e NT 2026.004 no catálogo  | conforme cronograma oficial              | validação e schemas                      | evitar tipo numérico/regex rígida       |
| Tributos legados  | coexistência transitória                        | calendário legal                         | regras simultâneas                       | selecionar versão pela data/operação    |

Datas concretas devem ser copiadas dos cronogramas oficiais para o catálogo antes do planejamento de implementação; esta tabela não substitui a NT.

## Compatibilidade

API usa versão maior para breaking change; campos aditivos são preferidos. Regras e schemas têm versionamento independente da API. Política de depreciação informa prazo, alternativa, ambientes e telemetria de uso.
