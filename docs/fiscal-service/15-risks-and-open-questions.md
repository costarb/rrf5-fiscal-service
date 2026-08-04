# Riscos e perguntas em aberto

## Matriz de riscos

| Risco                            | Prob. | Impacto | Mitigação                                       | Responsável          |
| -------------------------------- | ----: | ------: | ----------------------------------------------- | -------------------- |
| Mudança de NT/schema             |  Alta |    Alto | catálogo, vigência, regressão e monitor oficial | Fiscal + engenharia  |
| Classificação fiscal incorreta   | Média | Crítico | aprovação contábil e versionamento              | Cliente/contador     |
| Certificado vencido              | Média |    Alto | alertas, rotação e runbook                      | Operação             |
| Indisponibilidade SEFAZ          |  Alta |    Alto | circuit breaker e contingência oficial          | SRE + fiscal         |
| Documento duplicado              | Média | Crítico | idempotência, número e consulta antes de retry  | Engenharia           |
| Perda/corrupção de XML           | Baixa | Crítico | storage versionado, hash, backup e restore      | SRE                  |
| Falha de contingência            | Média | Crítico | agente/persistência, testes reais e exceção     | Engenharia/operação  |
| Divergência de numeração         | Média | Crítico | lock por série, reconciliação e auditoria       | Engenharia/fiscal    |
| Vazamento A1/CSC                 | Baixa | Crítico | KMS/Vault, least privilege, sem exportação      | Segurança            |
| Cross-tenant                     | Baixa | Crítico | escopo em todas as chaves e testes              | Segurança/engenharia |
| Biblioteca desatualizada/licença | Média |    Alto | POC, SLA/licença, interface própria e fallback  | Arquitetura          |
| Diferenças estaduais             |  Alta |    Alto | adaptadores e rollout por UF                    | Fiscal               |
| Ambiente incorreto               | Baixa | Crítico | separação física/lógica e bloqueios             | Engenharia           |
| Reforma Tributária               |  Alta |    Alto | NT 2025.002+ versionada e fixtures por data     | Fiscal + produto     |
| Homologação insuficiente         | Média | Crítico | matriz real assinada antes do piloto            | Produto/contador     |

## Decisões não automatizáveis

NCM, CEST, CFOP, CST, CSOSN, origem, benefício, monofásico, ICMS-ST, FCP, alíquotas, IBS/CBS, regime e natureza da operação dependem de responsável fiscal. O sistema valida consistência e reutiliza perfil, mas não substitui assessoria.

## Perguntas fiscais

- Qual regime/CRT e cadastro fiscal do piloto? Quais perfis aprovados para cada produto/combinação?
- Qual momento de emissão para balcão, comanda, delivery e marketplace?
- Como tratar cupons, comissão, taxa de entrega, gorjeta, cancelamento tardio e devolução?

## Perguntas técnicas

- Resultado das POCs .NET/Java e biblioteca licenciável?
- Haverá agente local para contingência quando o BurgoOS perder o Fiscal Service?
- Qual broker, storage e cofre do provedor de implantação?

## Perguntas operacionais/produto

- Quem pode ativar contingência, corrigir configuração e renovar certificado?
- O Fiscal Service será produto comercial separado desde o MVP?
- Qual volume, janela de suporte e SLO contratado?

## Perguntas de segurança/infra

- Exigir mTLS/IP allowlist para clientes críticos?
- Região, residência de dados, RPO/RTO e política de disaster recovery?
- Prazo aprovado para logs, auditoria e artefatos fiscais?

As perguntas não bloqueiam a especificação; as premissas atuais estão no `spec.md` e devem ser revisadas antes do plano de produção.
