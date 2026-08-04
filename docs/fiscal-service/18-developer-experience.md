# Experiência do integrador

## Jornada sandbox

1. Criar tenant/aplicação e obter client credentials.
2. Cadastrar empresa e estabelecimento.
3. Carregar A1 de homologação e configurar CSC/série.
4. Cadastrar perfis aprovados.
5. Pré-validar e emitir a primeira NFC-e.
6. Consultar, baixar XML/DANFE e verificar webhook.

Entregas: portal, OpenAPI 3.1, coleção Postman, catálogo de erros/rejeições, quickstart, sandbox/dados fictícios, status page, changelog, guia de idempotência/webhooks, política de versionamento/depreciação e SDKs após estabilizar a API.

## cURL

```bash
curl -X POST https://sandbox.fiscal.example/api/v1/documents \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: order-123-attempt-1" \
  -H "Content-Type: application/json" \
  --data @document.json
```

## TypeScript

```ts
const response = await fetch(`${baseUrl}/api/v1/documents`, {
  method: "POST",
  headers: {
    Authorization: `Bearer ${token}`,
    "Idempotency-Key": "order-123-attempt-1",
    "Content-Type": "application/json",
  },
  body: JSON.stringify(document),
});
const result = await response.json();
```

## C#

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents");
request.Headers.Authorization = new("Bearer", token);
request.Headers.Add("Idempotency-Key", "order-123-attempt-1");
request.Content = JsonContent.Create(document);
var response = await client.SendAsync(request);
```

## Java

```java
var request = HttpRequest.newBuilder(uri.resolve("/api/v1/documents"))
    .header("Authorization", "Bearer " + token)
    .header("Idempotency-Key", "order-123-attempt-1")
    .header("Content-Type", "application/json")
    .POST(HttpRequest.BodyPublishers.ofString(json))
    .build();
var response = client.send(request, HttpResponse.BodyHandlers.ofString());
```

Clientes devem tratar `202`, usar `Location`, persistir `documentId`, deduplicar webhooks e consultar o estado. Exemplos nunca incluem secrets, A1 ou classificação fiscal real.
