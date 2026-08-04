# Estratégia de testes

## Pirâmide e gates

1. Unitários: totais, arredondamento, regra, estado, idempotência, numeração e mapper SEFAZ.
2. Schema/XML: golden files, XSD oficial por hash, assinatura e canonicalização.
3. Contrato: OpenAPI, compatibilidade e webhooks.
4. Integração: PostgreSQL, fila, storage, cofre e adaptador simulado.
5. Homologação: certificado/CSC reais de teste, autorização, rejeição, consulta, cancelamento, inutilização e contingência aplicável.
6. Segurança: tenant, scopes, SSRF, XXE, signature wrapping, replay, secret rotation e downloads.
7. Não funcionais: carga, concorrência de série, recuperação, backup e caos de rede.

## Caso fiscal versionado

Cada fixture contém: identificador, finalidade, entrada normalizada, estabelecimento/regime, regra e aprovação fiscal, XML esperado sanitizado, schema/hash, NT/versão, resultado/código esperado, ambientes testados e data de revisão.

## Matriz mínima do piloto

- balcão anônimo, CPF, dinheiro, Pix, crédito, débito, misto e troco;
- desconto item/total, acréscimo, entrega, combo/adicionais e bebida com perfil distinto;
- marketplace/intermediador conforme configuração validada;
- certificado errado/vencido, CSC inválido, total divergente e schema inválido;
- 100 concorrências na mesma série sem duplicidade;
- timeout após autorização e replay idempotente;
- cancelamento válido/inválido, inutilização e lacuna;
- contingência, impressão, retorno, transmissão e rejeição posterior;
- dois tenants com tentativas cruzadas;
- atualização de NT com regressão de documentos anteriores.

Produção exige assinatura do contador nos casos tributários, engenharia nos resultados técnicos e operação no runbook.
