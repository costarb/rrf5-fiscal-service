# Catálogo de cenários de lanchonete e Food Truck

Cada cenário abaixo gera fixtures detalhadas antes da implementação. “Validar” significa confirmar documento, campos e tratamento com fonte oficial vigente e responsável fiscal; não assumir pelo exemplo.

|   # | Cenário                      | Documento/entrada essencial                                        | Fluxo e cuidados                              | Rejeições/testes prioritários      |
| --: | ---------------------------- | ------------------------------------------------------------------ | --------------------------------------------- | ---------------------------------- |
|   1 | Balcão presencial            | NFC-e; itens, presença, pagamento                                  | emitir no gatilho configurado                 | totais, presença, autorização      |
|   2 | Dinheiro                     | NFC-e; valor e troco                                               | pagamento fecha total                         | meio/troco divergente              |
|   3 | Pix                          | NFC-e; valor e integração aplicável                                | mapear tabela vigente                         | código/dados de pagamento          |
|   4 | Cartão                       | NFC-e; crédito/débito, credenciadora/autorização quando exigidos   | distinguir integrado/não integrado            | credenciadora/bandeira ausente     |
|   5 | Pagamento misto              | NFC-e; múltiplos pagamentos                                        | soma e troco coerentes                        | diferença de centavos              |
|   6 | Consumidor anônimo           | NFC-e; sem identificação quando permitido                          | minimizar PII                                 | identificação indevida/obrigatória |
|   7 | CPF na nota                  | NFC-e; CPF validado                                                | consentimento operacional e LGPD              | CPF formato inválido               |
|   8 | iFood                        | NFC-e ou NF-e conforme operação; intermediador/canal               | validar responsabilidade, descontos e entrega | intermediador/presença             |
|   9 | Aplicativo próprio           | NFC-e conforme operação; canal                                     | distinguir não presencial/retirada            | indicador incompatível             |
|  10 | Retirada                     | NFC-e; canal e presença                                            | emissão pelo estabelecimento vendedor         | local/presença                     |
|  11 | Entrega própria              | NFC-e conforme regra; taxa/endereço se aplicável                   | taxa como componente configurado              | total/endereço                     |
|  12 | Entrega plataforma           | NFC-e conforme regra; intermediador/taxa                           | separar venda e comissão                      | desconto/taxa duplicados           |
|  13 | Cancelado antes de emitir    | nenhum DF-e                                                        | cancelar comercialmente, não numerar          | provar ausência de documento       |
|  14 | Cancelado após autorização   | evento de cancelamento                                             | prazo/justificativa oficiais                  | prazo, estado e justificativa      |
|  15 | Desconto por item            | NFC-e; desconto por linha                                          | totalizadores coerentes                       | desconto acima do item             |
|  16 | Desconto total               | NFC-e; rateio/regra aplicável                                      | preservar reconciliação                       | total divergente                   |
|  17 | Acréscimo                    | NFC-e; componentes do total                                        | não inventar campo/classificação              | soma divergente                    |
|  18 | Taxa de entrega              | NFC-e/NF-e conforme operação                                       | perfil e composição aprovados                 | tributação/total                   |
|  19 | Comissão marketplace         | normalmente fora do valor recebido pelo emitente conforme contrato | não reduzir venda silenciosamente             | conciliação comercial-fiscal       |
|  20 | Cupom da loja                | desconto financiado pela loja                                      | refletir desconto conforme regra              | rateio e total                     |
|  21 | Cupom da plataforma          | dados de subsídio/recebível                                        | validar tratamento com contador/contrato      | valor da operação divergente       |
|  22 | Gorjeta/taxa adicional       | campo/composição conforme regra vigente                            | separar venda, serviço e repasse              | documento/modelo inadequado        |
|  23 | Emissão pós-pagamento        | NFC-e no gatilho configurado                                       | evitar janela sem emissão                     | replay após pagamento              |
|  24 | Internet do food truck falha | NFC-e contingência somente se elegível                             | agente/local ou bloqueio seguro               | perda local/relógio                |
|  25 | Contingência offline         | NFC-e; modalidade, horário e justificativa                         | DANFE e transmissão oficiais                  | tpEmis/QR/CSC                      |
|  26 | Retorno da conexão           | documento original                                                 | transmitir ordenadamente                      | duplicidade                        |
|  27 | Sincronizar pendentes        | lote operacional, documentos individuais                           | backoff/rate limit/consulta                   | fora do prazo                      |
|  28 | Retry duplicado              | idempotência + consulta                                            | devolver recurso original                     | hash diferente                     |
|  29 | Troca de equipamento         | mesma configuração central ou agente registrado                    | reconciliar sequência local                   | lacunas/clock/config               |
|  30 | Múltiplos caixas/série       | NFC-e; série central                                               | lock/atribuição serial                        | números repetidos                  |
|  31 | Pessoa jurídica              | avaliar NF-e 55 ou operação permitida                              | destinatário identificado e regras próprias   | modelo/IE/endereço                 |
|  32 | Devolução                    | NF-e 55 futura/referência                                          | finalidade/CFOP aprovados                     | chave/referência                   |
|  33 | Bonificação                  | documento e natureza validados                                     | valor/tributação não inferidos                | valor zero/regras fiscais          |
|  34 | Combo                        | itens fiscais segundo perfil aprovado                              | comercial pode decompor ou mapear perfil      | soma e classificação               |
|  35 | Adicionais                   | itens/acréscimos fiscais definidos                                 | snapshot da personalização                    | diferença pedido-nota              |
|  36 | Composição técnica           | não é regra fiscal por si                                          | ficha técnica não substitui NCM/perfil        | teste de separação de domínios     |
|  37 | Bebida distinta              | item com perfil próprio                                            | não herdar perfil do lanche                   | CST/NCM/perfil errado              |
|  38 | Monofásico                   | perfil aprovado e versão vigente                                   | contador obrigatório                          | regra/alíquota incompatível        |
|  39 | Cancelamento fora do prazo   | evento possivelmente rejeitado                                     | não forçar; abrir orientação/exceção          | rejeição oficial esperada          |
|  40 | Correção por evento          | CC-e onde legalmente permitida, tipicamente NF-e                   | bloquear campos não corrigíveis               | evento/modelo/campo inválido       |

## Estrutura de teste por cenário

Cada linha evolui para: documento aplicável confirmado, dados obrigatórios, perfil/versão, sequência, XML golden, códigos esperados, rejeições reais, homologação, responsável fiscal e evidência. Cenários 8, 12, 18–22, 31–40 não entram automaticamente no MVP sem decisão fiscal específica.
