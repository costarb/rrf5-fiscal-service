# Roadmap de implementação

## Fase 0 — Descoberta e POCs

**Objetivo:** eliminar riscos de protocolo e stack. **Entregas:** matriz oficial, casos do contador, POC A1/assinatura/XSD/SOAP/DANFE em .NET e Java, homologação SP, decisão de biblioteca/licença. **Aceite:** ao menos uma requisição assinada validada em homologação ou simulador oficial e ADRs aprovados. **Risco:** escolher stack por familiaridade.

## Fase 1 — Fundação independente

Tenants, aplicações OAuth, empresas, estabelecimentos, ambientes, cofre A1, storage imutável, auditoria, observabilidade, idempotência e CI regulatória. Demonstra onboarding seguro de dois tenants, sem emissão.

## Fase 2 — NFC-e homologação

Pré-validação, perfil fiscal básico, numeração, XML/XSD/assinatura, autorização/consulta, pagamentos, QR Code/DANFE e rejeições. Aceite: matriz de vendas do food truck autorizada/rejeitada como esperado.

## Fase 3 — Operação NFC-e

Cancelamento, inutilização, webhooks, contingência oficial, reconciliação, painel, runbooks, backup e produção limitada. Aceite: turno piloto exercitado e recuperação comprovada.

## Fase 4 — NF-e 55

Destinatário, transporte, interestadual, referências, devolução, complementar, eventos e CC-e. Aceite por suíte fiscal própria e homologação.

## Fase 5 — Escala nacional

Catálogo/adaptadores por UF, status de autorizadores, regras regionais e onboarding automatizado. Só iniciar após demanda real e capacidade de manutenção regulatória.

## Fase 6 — Extensões

NFS-e por domínio/adaptadores separados, A3/PAA, SDKs, parceiros e produto SaaS ampliado.

## Integração BurgoOS

Em paralelo às fases 1–3: cliente OAuth, snapshot fiscal do pedido/pagamento, outbox de solicitação, projeção `FiscalDocumentReference`, webhook idempotente, tela de estado/download e política de gatilho (pagamento, entrega ou decisão configurada).

## Gates de promoção

Local → CI → ambiente interno → homologação SEFAZ → piloto → produção limitada → expansão. Certificados, CSC, endpoints, números, séries, storage e credenciais são distintos em cada ambiente.
