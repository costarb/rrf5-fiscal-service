# Arquitetura de referência

## Escolha

**Novo repositório + monólito modular**. API e workers podem escalar/implantar separadamente, mas compartilham domínio e banco no MVP. Extração para microsserviços só ocorrerá por evidência de escala, isolamento ou equipe.

## Contexto

```mermaid
flowchart LR
  B[BurgoOS] -->|OAuth2 + REST| F[RRF5 Fiscal Service]
  T[ERPs e PDVs terceiros] -->|OAuth2 + REST| F
  O[Operação fiscal] -->|Painel| F
  F -->|SOAP/TLS + A1| S[SEFAZ / autorizadores]
  F -->|webhooks assinados| B
  F -->|webhooks assinados| T
```

## Contêineres

```mermaid
flowchart TB
  C[Clientes] --> G[API REST / Auth / Rate limit]
  G --> A[Aplicação fiscal modular]
  A --> DB[(PostgreSQL)]
  A --> Q[(Fila)]
  Q --> W[Workers emissão/eventos/webhooks]
  W --> DB
  W --> O[(Object storage imutável)]
  W --> K[Cofre/KMS]
  W --> S[Adaptadores SEFAZ]
  A --> R[(Cache/locks efêmeros)]
  A --> OT[Logs métricas traces]
```

## Componentes

- Identity & Tenant Access
- Companies & Establishments
- Certificate Vault
- Fiscal Configuration & Tax Rule Versions
- Document Intake & Normalization
- Numbering & Idempotency
- XML/XSD/Signature Pipeline
- Document Model Adapters (65; depois 55)
- Authorizer/UF Adapters
- Emission Orchestrator & Contingency
- Document Store & DANFE
- Events, Webhooks, Audit & Operations
- Regulatory Catalog

## Sequência de emissão

```mermaid
sequenceDiagram
  participant C as Cliente
  participant A as API
  participant D as Domínio/DB
  participant W as Worker
  participant S as SEFAZ
  C->>A: POST documento + Idempotency-Key
  A->>D: claim + hash + snapshot
  A-->>C: resultado ou 202
  W->>D: reservar número/transição
  W->>W: gerar, XSD, assinar, validar
  W->>S: autorizar
  alt autorizado
    S-->>W: protocolo
    W->>D: persistir estado e hashes
    W->>D: armazenar XML processado/DANFE
  else rejeitado
    S-->>W: código e motivo
    W->>D: persistir rejeição
  else resultado incerto
    W->>S: consultar recibo/chave
  end
```

## Decisão de stack

Comparação inicial:

| Stack           | Pontos fortes                                                                            | Risco/custo                                                                    |
| --------------- | ---------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| Java/Spring     | XML, assinatura, SOAP e ecossistema corporativo maduros                                  | operação/tooling novo para a equipe atual                                      |
| C#/.NET         | excelente suporte criptográfico/XML, bibliotecas fiscais brasileiras e deployment maduro | stack diferente do BurgoOS                                                     |
| Node/TypeScript | experiência atual e contratos compartilháveis                                            | ecossistema fiscal/assinatura/DANFE exige mais validação e risco de biblioteca |

**Recomendação:** executar POCs comparáveis em .NET e Java antes da escolha final. Para o núcleo fiscal, a preferência inicial é **.NET LTS**, atrás de interfaces próprias, se a POC comprovar assinatura, schema, SOAP, DANFE, licença e atualização RTC. TypeScript permanece no cliente BurgoOS e pode servir ao portal; não deve ser escolhido só por familiaridade. A decisão final está no ADR-012.

## Infraestrutura MVP

PostgreSQL, Redis apenas para cache/locks auxiliares, fila durável (RabbitMQ ou gerenciada), storage S3-compatível com versionamento, cofre/KMS via interface, OpenTelemetry e containers. Sem Kubernetes obrigatório.
