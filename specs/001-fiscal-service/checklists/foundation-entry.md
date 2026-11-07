# Foundation Entry Checklist: RRF5 Fiscal Service

**Purpose**: Critérios verificáveis para abrir e avançar a `feature/002-fiscal-foundation`  
**Created**: 2026-11-07  
**Feature**: [spec.md](../spec.md) · origem dos itens: [discovery-results.md](../discovery-results.md) (L = limitação interna, E = bloqueio externo)

Cada item só é marcado com evidência: link para commit, documento em `pocs/evidence/`
ou registro equivalente. A documentação oficial e a configuração fiscal aprovada
prevalecem sobre este checklist.

## A. Antes de abrir a branch da fundação

- [x] Runtime decidido e aceito: .NET 10 LTS (ADR-012)
- [x] Estratégia de biblioteca fiscal aceita: implementação própria (ADR-011)
- [x] Limitações e bloqueios externos registrados (T033)
- [ ] Todos os testes, builds de container, validação OpenAPI e scans de segredo do `quickstart.md` passam (T035)
- [ ] Auditoria confirma que nenhum certificado, CSC, XML com dados pessoais ou credencial está versionado (T036)
- [ ] Sign-off da discovery registrado e merge em `develop` aprovado (T037)
- [x] Baseline normativa reconferida em fonte oficial: pacote vigente 010g v1.03; golden XML válido nele (E5, 2026-11-07)
- [ ] ZIP oficial do 010g v1.03 congelado com hash, URL, data e NTs em `normative-baseline.md` e `catalog.json` (E5)
- [ ] Pacote de schema validado pela SEFAZ-SP confirmado em fonte oficial, explicando `verAplic` `SP_NFCE_PL_009_V400` (E4)

## B. Antes da primeira emissão em homologação pela fundação

### Normativo e fiscal

- [ ] Golden XML regenerado e fixado contra a baseline confirmada em A (E4, E5)
- [ ] Seção do MOC 7.0 que define os algoritmos de assinatura registrada (L8)
- [ ] Tabela de rótulos de `tPag` do DANFE extraída de cópia congelada do MOC (L5)
- [ ] Versão de schema resolvida por autorizador, ambiente e data, nunca fixa no código (ADR-012)
- [ ] Arredondamento implementado pela regra do leiaute, com testes de borda reaproveitados da POC (ADR-012)

### Segurança e segredos

- [ ] A1 e CSC lidos só do cofre escolhido (KMS/Vault), nunca de arquivo na imagem ou variável de build; teste prova ausência em logs e camadas (L1)
- [ ] Raízes ICP-Brasil fixadas por autorizador, sem desligar validação TLS (ADR-012)
- [ ] Threat model revisado para o ambiente de deploy escolhido: IAM, exposição em memória, object lock e telemetria (L1)

### Plataforma e container

- [ ] Imagem de produção sobre base chiseled do .NET, não-root, com fuso `America/Sao_Paulo` funcionando (L3, ADR-012)
- [ ] Build de imagem, SBOM CycloneDX e scan no CI; falha em achado alto/crítico com correção disponível (L2, ADR-012)
- [ ] `danfe-selftest` passa dentro do container Linux (L3)
- [ ] `System.Drawing.Common` fixado em versão suportada ou removido da árvore publicada (L12)

### Contrato

- [ ] Ferramenta de lint OpenAPI sem achados altos/críticos (Node 22 + Redocly v2 ou alternativa) (L11)
- [ ] `FiscalDocument.status` com enum fechado alinhado à máquina de estados (L10)
- [ ] Respostas `401`, `403` e `429` e exemplos de polling não terminal no OpenAPI (L10)

## C. Antes da produção e do primeiro cliente

### Fiscal (responsáveis externos)

- [ ] Perfil fiscal ratificado pelo contador, incluindo IBS/CBS para CRT 1 em 2026 e CST 49 de PIS/COFINS (E1, T011)
- [ ] Fonte de `vTotTrib` escolhida, com licença e cadência registradas em `dependency-review.md` (E2)
- [ ] Revisão jurídica de redistribuição de artefatos normativos e avisos de terceiros concluída (E3)

### Capacidade comprovada

- [ ] Autorização executada de dentro do container contra a homologação SEFAZ-SP, com latência e timeout da 1ª tentativa medidos (L2)
- [ ] Cancelamento e inutilização implementados e homologados na SEFAZ-SP (L7)
- [ ] Resultado incerto tratado com consulta antes de reenvio, testado na fundação (ADR-012)
- [ ] DANFE impresso em impressora térmica e QR lido em papel real com a margem padrão (L4)
- [ ] Itens a critério da UF habilitados só com legislação SP confirmada, ou mantidos desligados (L6)
- [ ] Capacidade medida em Linux no ambiente-alvo, substituindo os números do notebook (L9)

### Contrato e integração

- [ ] Operações de assinatura de webhook e esquema normativo de evento e cabeçalhos definidos antes do consumidor do BurgoOS (L10)
- [ ] Política de compatibilidade da API publicada (L10)

### Contingência

- [ ] Interface e documentação dizem que a emissão fica indisponível quando o PDV não alcança o serviço; nenhuma fila técnica é chamada de contingência (`contingency-gates.md`)
- [ ] Contingência offline só entra no escopo com aprovação do responsável fiscal e os gates de `offline-agent-spike.md` (E6)

## Notes

- Itens de A bloqueiam abrir a branch; de B, a primeira emissão em homologação pela
  fundação; de C, produção. Nenhum item pode ser marcado por inferência ou por
  documentação de biblioteca de terceiros.
- Itens externos (E1–E6) têm responsável em `discovery-results.md`; registrar data e
  evidência ao fechar.
