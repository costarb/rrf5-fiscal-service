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
