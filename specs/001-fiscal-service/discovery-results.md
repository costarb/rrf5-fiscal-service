# Resultados da discovery — limitações e bloqueios externos (T033)

**Data:** 2026-11-07  
**Branch:** `feature/001-fiscal-discovery`  
**Escopo:** NFC-e modelo 65, SEFAZ-SP, ambiente de homologação

Este documento consolida o que a Fase 0 provou, o que ficou de fora de propósito e o que
depende de terceiros. Ele não aprova tratamento tributário: a documentação oficial e a
configuração fiscal aprovada prevalecem.

## 1. Decisões tomadas

| Decisão | Registro | Data |
| --- | --- | --- |
| Serviço independente, fronteira OpenAPI/OAuth/webhook | ADR-001 | 2026-11-06 |
| Implementação fiscal própria sobre as bibliotecas da plataforma; nenhuma biblioteca fiscal de terceiros no runtime | ADR-011 | 2026-11-07 |
| .NET 10 LTS; imagem de produção em base chiseled | ADR-012 | 2026-11-07 |
| MVP somente em nuvem; sem contingência offline | `pocs/evidence/contingency-gates.md` | — |

## 2. Gates por candidato (estado final)

| Gate | .NET 10 (escolhido) | Java 25 | TypeScript |
| --- | --- | --- | --- |
| XML determinístico + XSD 010e v1.02 | PASS | PASS (byte-idêntico) | PASS (byte-idêntico) |
| XMLDSig, verificação, mutação rejeitada | PASS (A1 real, aceita pela SEFAZ) | PASS (A1 real, idêntica à autorizada) | PASS (chave descartável) |
| SOAP mock + homologação SEFAZ-SP | PASS (cStat 100 + consulta) | não feito | só TLS |
| Resultado incerto: consultar antes de reenviar | PASS | simulação compartilhada (Node) | simulação compartilhada (Node) |
| DANFE NFC-e + QR v3 | PASS (online) | não feito | não feito |
| Container Linux | PASS (status com A1 real) | não feito | não feito |
| SBOM, licenças, vulnerabilidades | PASS | SBOM limpo | SBOM limpo |

**Tarefas T016–T018** pedem a implementação "em cada candidato". Foram fechadas assim:
T016 e T017 foram cumpridas pelos três; T018 (SOAP mock e rastreio sanitizado) foi
cumprida só pelo candidato escolhido. O SOAP em Java e TypeScript não foi feito porque o
scorecard já os colocava atrás mesmo supondo esse gate concluído (Java 4,11 contra 4,74).
Isso é uma limitação aceita, não um gate reprovado.

## 3. Limitações das POCs (resolver na fundação, sem dependência externa)

| # | Limitação | Origem | Tratamento na fundação |
| --- | --- | --- | --- |
| L1 | Nenhum A1, CSC, tenant de produção ou documento fiscal real no repositório; IAM de nuvem, exposição em memória no KMS, object lock e telemetria em runtime não foram provados | `threat-model.md` | Gate de entrada: retestar no ambiente de deploy escolhido |
| L2 | `authorize` não foi executado dentro do container (só `status` com A1 real); latência no container e timeout da 1ª tentativa não caracterizados; build de container fora do CI | `container-poc.md` | Rodar autorização no container e medir; build e scan no CI |
| L3 | `danfe-selftest` não rodou em Linux; a imagem precisa de `tzdata` para `America/Sao_Paulo` | `danfe-qr.md` | Incluir tzdata (ou ICU/TZ equivalente na base chiseled) e rodar o selftest no container |
| L4 | Impressão do DANFE em impressora térmica e leitura do QR em papel real com a margem padrão de 30 mm | `danfe-qr.md` | Teste físico antes do primeiro cliente |
| L5 | Tabela de rótulos de `tPag` no DANFE não conferida com cópia congelada do MOC | `danfe-qr.md` | Congelar a tabela do MOC e substituir a tabela curta |
| L6 | Itens a critério da UF (`xMsg`, acréscimos/descontos por item, DANFE resumido) desligados | `danfe-qr.md` | Habilitar só com legislação SP confirmada |
| L7 | Eventos (cancelamento, inutilização) não implementados nem homologados | ADR-011 | Maior risco do caminho próprio; homologar antes do MVP |
| L8 | Referência de seção do MOC 7.0 para os algoritmos de assinatura não registrada (PDF ausente no host); os algoritmos estão provados pela autorização da SEFAZ | `xmldsig-poc.md` | Registrar a seção ao congelar o MOC na fundação |
| L9 | Benchmark em notebook Windows; números são razões, não capacidade de produção | `performance-bench.md` | Medir capacidade em Linux no ambiente-alvo |
| L10 | Contrato OpenAPI sem operações de assinatura de webhook, esquema normativo de evento/cabeçalhos e enum de `FiscalDocument.status`; faltam `429`, `401/403`, exemplos de polling e política de compatibilidade | `burgoos-contract-review.md` | Fechar antes de implementar o consumidor de webhook do BurgoOS |
| L11 | Ferramenta de lint do contrato (Redocly v1) com 3 achados altos de desenvolvimento | `dependency-review.md` | Migrar para Node 22 + Redocly v2 ou outro validador OpenAPI 3.1 |
| L12 | `System.Drawing.Common` presente na árvore só por causa do QRCoder | `dependency-review.md` | Remover da árvore publicada quando possível |

## 4. Bloqueios externos (dependem de terceiros)

| # | Bloqueio | Quem resolve | Impacto |
| --- | --- | --- | --- |
| E1 | Ratificação do perfil fiscal pelo contador (T011), incluindo a omissão de IBS/CBS para emitente CRT 1 em 2026 e a escolha de CST 49 para PIS/COFINS | Contador; prevista para a semana de 2026-11-09 | Bloqueia emissão em produção; não bloqueia a stack |
| E2 | Fonte de `vTotTrib` (IBPT ou outra): tabela, licença e cadência de atualização | Operador + contador | Bloqueia o cálculo exibido no DANFE em produção |
| E3 | Revisão jurídica de redistribuição dos artefatos normativos e dos avisos empacotados (libxml2 no `xmllint-wasm`, pacotes do SO na imagem) | Jurídico/comercial | Bloqueia distribuição; artefatos normativos seguem fora do Git |
| E4 | Confirmar no portal oficial qual pacote de schema a SEFAZ-SP valida: `verAplic` informa `SP_NFCE_PL_009_V400` e a baseline congelada é 010e v1.02 | Operador, via portal SEFAZ-SP/NF-e | Bloqueia fixar o comportamento do golden XML em produção |
| E5 | Pacote vigente confirmado pelo operador em 2026-11-07: **010g v1.03** (golden XML válido nele). Falta o ZIP oficial, com URL, data e NTs, para congelar o hash (`normative-baseline.md`) | Operador, via portal NF-e | Bloqueia trocar o hash fixado nas POCs e na fundação |
| E6 | Aplicabilidade, ativação e saída da contingência offline da NFC-e/SP | Responsável fiscal | Contingência continua fora do MVP até aprovação |

## 5. Não-objetivos mantidos

Seguem válidos os não-objetivos de `contingency-gates.md` e `offline-agent-spike.md`:
nada de contingência offline ou EPEC em produção, nada de troca automática para modo
offline em timeout HTTP, nada de nova numeração ou chave após impressão ou resposta
incerta, e nenhuma fila técnica apresentada como contingência oficial. Quando o PDV não
alcança o serviço, a emissão fiscal fica indisponível e a interface diz isso.

## 6. Próximos passos da Fase 0

T034 (checklist de entrada da fundação), T035 (rodar todos os testes e builds do
`quickstart.md`), T036 (auditoria do repositório) e T037 (sign-off). Os itens L1–L12
alimentam o checklist de entrada; E1–E6 entram como pré-condições com responsável.
