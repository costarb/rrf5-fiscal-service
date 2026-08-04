# Research: Fiscal Service Discovery

**Data-base**: 2026-08-04

## Runtime

### Decision

Comparar .NET 10 LTS e Java 25 LTS; manter .NET 10 como candidato preferencial. TypeScript é baseline, não escolha presumida.

### Rationale

.NET 10 está em suporte ativo até 14/11/2028 e oferece APIs maduras de certificados, XML, assinatura e HTTP. Java 25 é LTS e possui ecossistema criptográfico/XML maduro, porém introduz stack operacional adicional. TypeScript coincide com a experiência BurgoOS, mas o custo/risco de protocolo fiscal precisa ser demonstrado, não ocultado.

### Alternatives considered

.NET 8 foi descartado para projeto novo porque termina suporte em 10/11/2026. Java 21 permanece tecnicamente viável, mas Java 25 é a linha LTS atual. Implementação fiscal dentro do BurgoOS foi rejeitada por acoplamento.

## Fiscal library strategy

### Decision

Avaliar Unimake.DFe na POC .NET e pelo menos uma opção Java com manutenção/licença comprovadas. Nenhuma biblioteca é aprovada antes dos gates.

### Rationale

Unimake.DFe apresenta atividade recente e licença MIT, além de declarar NF-e/NFC-e. Isso reduz risco inicial, mas não prova cobertura da NT 2025.002, DANFE/QR, contingência e homologação SP.

### Alternatives considered

Implementação integral própria oferece controle, porém transfere todo risco de schema, assinatura, SOAP, DANFE e NT à equipe. Provedor fiscal SaaS externo reduz implementação, mas muda produto, custo e dependência; é fallback se POCs falharem.

## Persistence and messaging

### Decision

PostgreSQL, object storage S3-compatível, fila durável e KMS/Vault por interfaces. Redis somente para cache/locks auxiliares; consistência fiscal permanece no banco.

### Rationale

Número, idempotência e estado exigem transação forte. XML/PDF precisam de retenção e versionamento. Jobs/webhooks precisam de entrega pelo menos uma vez e consumidores idempotentes.

## Numbering

### Decision

Alocar número após validação e imediatamente antes da assinatura, sob lock/transação da série. Nunca decrementar. Lacunas são registradas e tratadas por inutilização quando aplicável.

### Rationale

Reduz reserva desperdiçada sem permitir colisão entre workers. Reserva antecipada ampla aumenta lacunas; número no cliente impede controle multiaplicação.

## A1 and secret management

### Decision

Envelope encryption e `CertificateVault` independente de provedor; senha/chave nunca no banco em claro ou logs. POC usa secret local/CI fora do Git.

### Rationale

Permite AWS/Azure/GCP/Vault e futuro A3/PAA sem alterar o domínio.

## SEFAZ integration

### Decision

Adaptadores por modelo/autorizador resolvidos por catálogo versionado. Resultado incerto sempre consulta antes de retransmitir.

### Rationale

Endpoints, capacidades e cronogramas mudam. A queda após autorização é o cenário de duplicidade mais crítico.

## Contingency

### Decision

Não implementar contingência offline completa na Fase 0; criar spike arquitetural de agente local e provar preservação/idempotência. A fundação cloud não prometerá operar se o PDV perder o Fiscal Service.

### Rationale

Fila no servidor não resolve perda de conectividade PDV-serviço. O manual oficial exige documento, impressão e transmissão posterior específicos.

## Official references baseline

- Portal Nacional: MOC 7.0 e leiaute 4.00, complementados por NTs.
- NT 2025.002 v1.40 publicada em 20/05/2026.
- DANFE NFC-e/QR Code v6.0.
- Contingência offline NFC-e v2.0.
- Web Services e orientações NFC-e/SP revalidados antes da POC.

Cada download da POC deve registrar URL, timestamp, versão e SHA-256; não copiar artefato normativo para produção sem licença/condição de redistribuição revisada.
