# RRF5 Fiscal Service — Resumo executivo

**Data-base normativa da análise:** 04/08/2026.

## Decisão recomendada

Criar **outro projeto e repositório** para o RRF5 Fiscal Service. Ele será um produto independente consumido pelo BurgoOS e por terceiros via API. A primeira versão deve ser um **monólito modular**, com API e workers separados, banco, fila, object storage e gestão de segredos próprios. Dividir imediatamente em vários microsserviços aumentaria custo e risco operacional sem benefício proporcional.

## Por que não incorporar ao BurgoOS

- disponibilidade fiscal e contingência têm ciclo diferente do pedido;
- certificado A1 e CSC elevam substancialmente a criticidade de segurança;
- XML/protocolo exigem guarda, integridade e recuperação independentes;
- schemas, regras e Notas Técnicas mudam por vigência;
- numeração exige concorrência e consistência próprias;
- o serviço precisa atender outros ERPs/PDVs sem depender do modelo interno do BurgoOS;
- o BurgoOS atual não possui fila, cache, object storage fiscal ou cofre de certificados.

## Escopo do MVP

Inclui NFC-e 65 em São Paulo, A1, homologação/produção, configuração por estabelecimento, perfis fiscais básicos, pré-validação, emissão síncrona/assíncrona, consulta, cancelamento, inutilização, DANFE/QR Code, idempotência, webhooks, auditoria, observabilidade e contingência oficial necessária à operação real.

Ficam fora: NF-e 55 completa, NFS-e, A3, cobertura nacional, motor de recomendação tributária, Kubernetes obrigatório e SDKs para todas as linguagens.

## Integração com o BurgoOS

O BurgoOS envia um snapshot comercial após a definição do fato gerador configurado, incluindo pedido, itens, descontos, adicionais, pagamento e referências. Recebe `documentId`, estado e links; persiste apenas referências e projeção operacional. XML, protocolo, certificado, série e regras permanecem no Fiscal Service. Webhook invalida a projeção, mas a confirmação final sempre ocorre por consulta.

## Condições para produção

Produção exige credenciamento/CSC, certificado válido, série e numeração reconciliadas, perfis aprovados pelo contador, suíte de homologação, contingência exercitada, backup/restauração testados e monitoramento ativo.
