# ADR-005 — Processamento síncrono e assíncrono

**Status:** Proposto; detalhado em 2026-11-12 para a feature 005, aguarda aceite do operador
**Escopo:** emissão NFC-e (005, FE-010, FE-011); FR-005 e FR-007 da 001

## Decisão original

Uma única máquina persistente atende os dois modos. O síncrono espera até um limite configurado e degrada para `202`; o assíncrono retorna imediatamente. Workers consomem uma fila durável com entrega pelo menos uma vez.

## Detalhamento (2026-11-12)

O operador decidiu os dois modos em 2026-11-12.

1. **Entrada única:** síncrono e assíncrono chamam o mesmo caso de uso.
   - Ele reivindica a idempotência (chave + hash canônico do pedido, FF-003) e grava o documento em `VALIDATING`.
   - Na **mesma transação**, grava uma mensagem na tabela `outbox`.
   - Nenhuma chamada à SEFAZ acontece dentro de uma requisição HTTP.
2. **Outbox → RabbitMQ:** um publicador no worker lê a `outbox` e publica na fila durável.
   - Publica com confirmação do broker e marca a mensagem como enviada só depois dela.
   - Se cair, republica: a entrega é pelo menos uma vez.
3. **Worker idempotente pelo estado:**
   - o consumidor trava a linha do documento (`select … for update skip locked`) e só avança a partir do estado esperado;
   - uma mensagem duplicada ou atrasada encontra outro estado e é descartada;
   - o A1 e o CSC só são abertos aqui (política `rrf5-worker`, ADR-004).
4. **Síncrono:**
   - a API espera o documento chegar a `AUTHORIZED` ou `REJECTED` até um tempo-limite configurável por ambiente;
   - nessa espera ela lê o estado do banco, com notificação local para não sondar à toa;
   - no tempo-limite, responde `202` com o recurso não terminal (`Location` + estado) em vez de erro;
   - o cliente segue por polling, como no assíncrono.
5. **Assíncrono:** responde `202` logo depois do commit, com o recurso.
6. **Resultado incerto (FR-007):**
   - timeout de transporte, resposta ilegível ou queda depois do envio deixam o documento em `WAITING_SEFAZ`, o estado do enum fechado;
   - um job do worker consulta por chave antes de qualquer reenvio, com backoff e limite;
   - consulta indisponível mantém `WAITING_SEFAZ` e gera alerta;
   - **retentativa técnica nunca é chamada de contingência** (ADR-010).
7. **Transições** pelo enum fechado do `data-model.md`, garantidas também no banco (005 T008).

## Consequências

- **Positivas:**
  - nenhuma lógica fiscal fica duplicada entre os dois modos;
  - a API não segura conexão com a SEFAZ nem abre o A1;
  - a queda do broker não perde emissões, porque a `outbox` fica no PostgreSQL.
- **Custos:**
  - mais um componente no caminho, entre a `outbox` e o RabbitMQ;
  - o síncrono ganha latência de fila, medida contra SE-005 (500 ms locais, sem contar a SEFAZ).
- **Clientes:** devem tratar `202` também no modo síncrono e consultar o estado.

## Alternativas consideradas

- **Chamar a SEFAZ na própria requisição síncrona:** menor latência, mas a API precisaria do A1 (contraria o ADR-004 e a política `rrf5-api`), e uma queda no meio deixaria o resultado incerto sem dono.
- **Fila sem outbox:** perde a mensagem se o processo cair entre o commit e a publicação.
