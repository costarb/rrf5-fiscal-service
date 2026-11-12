# ADR-013 — Emissor de token

**Status:** Aceito pelo operador em 2026-11-10  
**Data:** 2026-11-10  
**Escopo:** autenticação máquina-a-máquina da fundação (FF-001); sem usuários humanos nesta fase

## Contexto

A API exige OAuth 2.0 client credentials com scopes (FR-019). A fundação roda
local primeiro (D3), administração só por API (D4) e todo principal é uma
aplicação, inclusive o aprovador fiscal (D5). Não há painel nem login humano.

## Decisão

1. Endpoint próprio `POST /oauth/token`, **só** `grant_type=client_credentials`.
   Sem refresh token, sem authorization code, sem senha de usuário.
2. Segredo de aplicação: 32 bytes aleatórios, mostrado uma única vez, guardado
   como PBKDF2-SHA256 (`Rfc2898DeriveBytes.Pbkdf2`, 600.000 iterações, salt por
   aplicação). Rotação cria segredo novo e revoga o anterior.
3. Token JWT de 10 minutos, assinado com ECDSA P-256 pela engine transit do
   OpenBao (a chave privada nunca sai do cofre). Claims: `sub` (aplicação),
   `tenant_id`, `scope`, `aud`, `iss`, `jti`, `iat`, `exp`.
4. Chaves públicas em `/.well-known/jwks.json`; a API valida em processo com
   `Microsoft.AspNetCore.Authentication.JwtBearer`, com cache das chaves.
5. Limite de taxa no endpoint de token e auditoria de emissões e falhas.
6. Scopes administrativos separados dos de documento; o scope de aprovação
   fiscal (`fiscal.rules.approve`) não é concedido junto com o de autoria.

## Alternativas

- **IdP externo (Keycloak)**: mais recursos (MFA, usuários humanos), mais um
  serviço para operar e proteger. Reavaliar quando o painel trouxer login humano.
- **IdP da nuvem (Cognito, Entra ID)**: depende do provedor, ainda não escolhido;
  reavaliar junto com essa escolha (checklist B).

## Consequências

- Superfície sensível no próprio serviço: testes obrigatórios de expiração,
  assinatura inválida, `alg` trocado, `aud`/`iss` errados, segredo rotacionado e
  chave do cofre indisponível (falha fechada).
- Assinar cada token no cofre adiciona latência ao endpoint de token, não às
  chamadas de negócio.
- A troca futura por um IdP externo afeta só emissão e validação de token; scopes
  e autorização por tenant e estabelecimento continuam iguais.

## Implementação (2026-11-11, T014–T017)

- **Credencial:**
  - `client_id` no formato `app_` mais 32 hex;
  - o segredo tem o prefixo `rrf5s_`, para que scanners de segredo o reconheçam;
  - o endpoint aceita `client_secret_basic` ou `client_secret_post`, nunca os dois na mesma requisição;
  - um cliente desconhecido gasta o mesmo tempo de PBKDF2 que um conhecido.
- **Busca do cliente antes de saber o tenant:**
  - a função `auth_find_client` (migração 0008) é `SECURITY DEFINER` e pertence ao papel `rrf5_auth`, que é `NOLOGIN`;
  - esse papel tem uma policy de leitura só para si e o `rrf5_app` não é membro dele;
  - a API pode chamar a função, mas continua sem ler a tabela de outros tenants.
- **Token:**
  - claims `iss`, `aud`, `sub`, `tenant_id`, `scope`, `jti`, `iat`, `nbf` e `exp`;
  - `kid` no formato `rrf5-token-signing-v<versão>`;
  - assinatura ES256 pela transit, com `marshaling_algorithm=jws`;
  - as chaves públicas ficam em cache por 5 minutos, e um `kid` desconhecido força a releitura.
- **Validação:** só ES256, com `iss` e `aud` exatos, `typ` igual a JWT, expiração obrigatória e tolerância de 30 s de relógio. Sem chave no cofre, qualquer token dá 401. Sem cofre, o endpoint de token responde 503.
- **Escopos:**
  - `platform.admin` só existe no tenant `PLATFORM`, com checagem na aplicação e no banco;
  - `fiscal.rules.write` e `fiscal.rules.approve` nunca vão na mesma aplicação;
  - o primeiro administrador sai do comando `bootstrap-platform-admin`.
- **Limite de taxa:** 30 pedidos de token por minuto por endereço (`RateLimits:TokenPerMinute`).
- **Limite conhecido:** suspender uma aplicação ou um tenant, ou girar o segredo, impede novos tokens, mas não revoga os já emitidos, que valem por até 10 minutos. Revogação imediata, se for necessária, entra junto com o painel.
